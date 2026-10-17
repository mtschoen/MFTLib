namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     How many scans of one drive in a row may lose their journal catch-up before a scan
    ///     operation stops rescanning the drive by itself.
    /// </summary>
    internal const int LostCatchUpRecoveryLimit = 3;

    /// <summary>
    ///     A test seam: invoked with the drive while a commit holds the drive's write gate,
    ///     before it takes the state lock to publish.
    /// </summary>
    internal Action<DriveKey>? PublishInsideWriteGateForTest { get; set; }

    /// <summary>Why a published block's journal cursor cannot be resumed by a watch.</summary>
    enum UnresumableCheckpointReason
    {
        /// <summary>A cache-only open adopted the block despite a lost checkpoint.</summary>
        CacheOnlyAdoption,

        /// <summary>The scan that produced the block lost its journal catch-up.</summary>
        LostCatchUp
    }

    /// <summary>
    ///     Unresumable blocks by ordinal, with the reason. <see cref="StartWatchingAsync" /> refuses
    ///     such a drive's watch, since starting it from that block's cursor would resume from a
    ///     position the journal no longer holds. A scan whose catch-up holds replaces the block and
    ///     clears the entry.
    /// </summary>
    readonly Dictionary<ushort, UnresumableCheckpointReason> _unresumableCheckpointsByOrdinal = [];

    /// <summary>
    ///     The scan operation every rescan of a drive runs with the drive's lifecycle gate held:
    ///     produce, publish, and, when the published block's catch-up was lost, raise
    ///     <see cref="WatchFaultKind.CatchUpLost" /> and scan again at once while the drive's count
    ///     is below <see cref="LostCatchUpRecoveryLimit" />. The fault is raised with no write gate
    ///     and no <see cref="_stateLock" /> held, while this operation still holds the lifecycle
    ///     gate; the first publication has retired and drained the old pump, so delivery between
    ///     retries stays serialized. Production before that publication keeps a healthy pump running. Only
    ///     the first attempt's publish of a manual rescan clears the drive's checkpoint-loss report,
    ///     so a retry keeps the report the loss produced, and a recovery (<paramref name="recovery" />)
    ///     keeps the report that explains why it rescanned. A recovery also re-checks that the watch
    ///     is still requested before each further attempt, and stops with the attempt it has once a
    ///     stop has cleared the request. Throws the last <see cref="JournalCatchUpLostException" />
    ///     when the count reaches the limit. Returns the final attempt.
    /// </summary>
    async Task<ScanAttempt> RunScanOperationAsync(DriveRuntime runtime, IndexedDrive drive,
        RecoveryTicket? recovery, CancellationToken cancellationToken)
    {
        try
        {
            var firstAttempt = true;
            while (true)
            {
                var attempt = await ScanAndPublishAsync(runtime, drive,
                    clearsCheckpointLoss: firstAttempt && recovery is null, recovery, cancellationToken)
                    .ConfigureAwait(false);
                firstAttempt = false;
                if (attempt.CatchUpLoss is not { } catchUpLoss)
                {
                    return attempt;
                }

                var lost = RecordLostCatchUp(runtime, catchUpLoss, attempt.ConsecutiveLostCatchUps);
                RaiseWatchFaulted(new WatchFault(WatchFaultKind.CatchUpLost, runtime.Drive, lost));
                if (lost.IsRecoveryStopped)
                {
                    throw lost;
                }

                if (recovery is not null && !IsWatchRequested(runtime))
                {
                    return attempt;
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                runtime.RetryingLostCatchUp = false;
                runtime.RetriedLostCatchUp = null;
                NoteWatchStateLocked(runtime);
            }

            RaiseWatchStateChanged(runtime);
        }
    }

    bool IsWatchRequested(DriveRuntime runtime)
    {
        lock (_stateLock)
        {
            return runtime.WatchRequested;
        }
    }

    /// <summary>
    ///     The open's settle of a drive it has to scan: the same scan operation a rescan runs, before
    ///     any snapshot exists. Each attempt is adopted under <see cref="_stateLock" />; a retry after
    ///     a lost catch-up scans again at once, replaces the lost block, which nothing has published,
    ///     at the same ordinal and closes it, and keeps the loss as the drive's report. At
    ///     <see cref="LostCatchUpRecoveryLimit" /> the drive settles with its last block, unresumable,
    ///     and the open does not throw. No fault is raised: no handler can be subscribed during
    ///     <see cref="OpenAsync" />, so the drive's status is the record. A first scan that produces
    ///     no block leaves the drive blockless; a retry that produces none leaves the lost block in
    ///     place with the producer's failure.
    /// </summary>
    async Task ScanOpenedDriveAsync(IndexedDrive drive, bool ownsCanonicalSlot, PendingDriveResult opened,
        CancellationToken cancellationToken)
    {
        var runtime = GetDriveRuntime(drive.Drive);
        DriveBlock? adopted = null;
        while (true)
        {
            var target = ComputeScanTarget(drive, ownsCanonicalSlot);
            var retired = RenameAsideForRescan(target, adopted);
            PendingDriveResult produced;
            try
            {
                produced = await ProduceDriveBlockAsync(drive, target.Path, target.DeleteOnClose, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                DiscardUnproducedTarget(drive, target, retired);
                throw;
            }

            var settled = produced with
            {
                CheckpointLoss = opened.CheckpointLoss,
                CacheSlot = opened.CacheSlot
            };
            if (settled.Block is not { } block)
            {
                DiscardUnproducedTarget(drive, target, retired);
                RecordOpenProducerFailure(runtime.Drive, settled, lostBlockInPlace: adopted is not null);
                return;
            }

            var (newlyAdopted, consecutiveLostCatchUps) = AdoptOpenedDrive(drive, block, settled, adopted);
            // Nothing published the lost block this retry replaced; releasing it deletes its renamed-aside file.
            adopted?.ReleaseState.Release();
            adopted = newlyAdopted;
            if (settled.CatchUpLoss is not { } catchUpLoss ||
                RecordLostCatchUp(runtime, catchUpLoss, consecutiveLostCatchUps).IsRecoveryStopped)
            {
                return;
            }

            opened = opened with { CheckpointLoss = catchUpLoss };
        }
    }

    void RecordOpenProducerFailure(DriveKey drive, PendingDriveResult failed, bool lostBlockInPlace)
    {
        if (lostBlockInPlace)
        {
            RecordRescanProducerFailure(drive, failed.RequireFailureMessage(drive), endsOpenSettle: true);
        }
        else
        {
            RecordFailedDrive(drive, DriveFailureKind.ProducerFailed, failed);
        }
    }

    /// <summary>
    ///     Publishes what a lost catch-up means for the drive before its fault is raised. Below the
    ///     limit a watched drive reads <see cref="WatchState.Recovering" /> while the next
    ///     scan runs. At the limit the drive's watch is refused with the exception as its failure,
    ///     so it reads <see cref="WatchState.Faulted" /> with the exception's message.
    /// </summary>
    JournalCatchUpLostException RecordLostCatchUp(DriveRuntime runtime, JournalCheckpointLoss catchUpLoss,
        int consecutiveLostCatchUps)
    {
        var drive = runtime.Drive;
        var recoveryStopped = consecutiveLostCatchUps >= LostCatchUpRecoveryLimit;
        var lost = new JournalCatchUpLostException(recoveryStopped,
            DescribeLostCatchUp(drive, consecutiveLostCatchUps, recoveryStopped, catchUpLoss));
        lock (_stateLock)
        {
            runtime.RetryingLostCatchUp = !recoveryStopped && runtime.WatchRequested;
            runtime.RetriedLostCatchUp = runtime.RetryingLostCatchUp ? lost : null;
            FaultRestartPendingWaiterLocked(runtime, lost);
            if (recoveryStopped)
            {
                runtime.RecoveryStopped = true;
                // A recovery that reaches the limit ends here, so the drive reads Faulted with
                // this loss as its cause now, before the loss is reported, rather than when the
                // recovery returns.
                if (runtime.RecoveryState == RecoveryState.Recovering)
                {
                    runtime.RecoveryState = RecoveryState.None;
                }

                runtime.RefusedStartFault = lost;
                if (TryGetDriveOrdinalLocked(drive, out var driveOrdinal))
                {
                    _watchFailureMessagesByOrdinal[driveOrdinal] = lost.Message;
                }
            }

            NoteWatchStateLocked(runtime, new WatchFault(WatchFaultKind.CatchUpLost, drive, lost));
        }

        RaiseWatchStateChanged(runtime);
        return lost;
    }

    static string DescribeLostCatchUp(DriveKey drive, int consecutiveLostCatchUps, bool recoveryStopped,
        JournalCheckpointLoss catchUpLoss)
    {
        if (!recoveryStopped)
        {
            return $"Drive {drive}: the journal no longer held the cursor armed before the scan when the " +
                   $"scan finished, so its catch-up was lost ({consecutiveLostCatchUps} in a row). The drive is " +
                   "being scanned again.";
        }

        var remedy = catchUpLoss.SizeThatWouldHaveRetained is { } size
            ? $"Grow the drive's USN journal to at least {size} bytes (BrokerSession.GrowUsnJournalAsync), " +
              "then call FileIndex.RescanAsync for this drive."
            : "Call FileIndex.RescanAsync for this drive.";
        return $"Drive {drive}: {consecutiveLostCatchUps} scans in a row lost their journal catch-up, so " +
               $"the drive keeps its last block, which cannot be watched. {remedy}";
    }
}
