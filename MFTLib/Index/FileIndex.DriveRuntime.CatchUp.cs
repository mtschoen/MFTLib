namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
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
        async Task<ScanAttempt> RunScanOperationAsync(
            RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            try
            {
                var firstAttempt = true;
                while (true)
                {
                    var attempt = await ScanAndPublishAsync(
                        clearsCheckpointLoss: firstAttempt && recovery is null, recovery, cancellationToken)
                        .ConfigureAwait(false);
                    firstAttempt = false;
                    if (attempt.CatchUpLoss is not { } catchUpLoss)
                    {
                        return attempt;
                    }

                    var lost = RecordLostCatchUp(catchUpLoss, attempt.ConsecutiveLostCatchUps);
                    RaiseWatchFaulted(new WatchFault(WatchFaultKind.CatchUpLost, Volume, lost));
                    if (lost.IsRecoveryStopped)
                    {
                        throw lost;
                    }

                    if (recovery is not null && !IsWatchRequested())
                    {
                        return attempt;
                    }
                }
            }
            finally
            {
                lock (StateLock)
                {
                    _retryingLostCatchUp = false;
                    _retriedLostCatchUp = null;
                    NoteWatchStateLocked();
                }

                RaiseWatchStateChanged();
            }
        }

        bool IsWatchRequested()
        {
            lock (StateLock)
            {
                return _watchRequested;
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
        async Task<int> ScanOpenedDriveAsync(bool ownsCanonicalSlot, PendingDriveResult opened,
            OpenSettlement settlement, CancellationToken cancellationToken)
        {
            DriveBlock? adopted = null;
            while (true)
            {
                var target = ComputeScanTarget(ownsCanonicalSlot);
                var retired = RenameAsideForRescan(target, adopted);
                PendingDriveResult produced;
                try
                {
                    produced = await ProduceDriveBlockAsync(target.Path, target.DeleteOnClose, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    DiscardUnproducedTarget(target, retired);
                    throw;
                }

                var settled = produced with
                {
                    CheckpointLoss = opened.CheckpointLoss,
                    CacheLocation = opened.CacheLocation
                };
                if (settled.Block is not { } block)
                {
                    DiscardUnproducedTarget(target, retired);
                    return RecordOpenProducerFailure(settled, lostBlockInPlace: adopted is not null, settlement);
                }

                var (newlyAdopted, consecutiveLostCatchUps, settledCount) = AdoptOpenedDrive(
                    block, settled, settlement, replacing: adopted);
                // Nothing published the lost block this retry replaced; releasing it deletes its renamed-aside file.
                adopted?.ReleaseState.ReleaseUnborrowed();
                adopted = newlyAdopted;
                if (settled.CatchUpLoss is not { } catchUpLoss ||
                    RecordLostCatchUp(catchUpLoss, consecutiveLostCatchUps).IsRecoveryStopped)
                {
                    return settledCount ?? throw new InvalidOperationException($"Drive {Drive} settled without its count.");
                }

                opened = opened with { CheckpointLoss = catchUpLoss };
            }
        }

        /// <summary>
        ///     Publishes what a lost catch-up means for the drive before its fault is raised. Below the
        ///     limit a watched drive reads <see cref="WatchState.Recovering" /> while the next
        ///     scan runs. At the limit the drive's watch is refused with the exception as its failure,
        ///     so it reads <see cref="WatchState.Faulted" /> with the exception's message.
        /// </summary>
        JournalCatchUpLostException RecordLostCatchUp(JournalCheckpointLoss catchUpLoss,
            int consecutiveLostCatchUps)
        {
            var drive = Volume;
            var recoveryStopped = consecutiveLostCatchUps >= LostCatchUpRecoveryLimit;
            var lost = new JournalCatchUpLostException(recoveryStopped,
                DescribeLostCatchUp(consecutiveLostCatchUps, recoveryStopped, catchUpLoss));
            lock (StateLock)
            {
                _retryingLostCatchUp = !recoveryStopped && _watchRequested;
                _retriedLostCatchUp = _retryingLostCatchUp ? lost : null;
                FaultRestartPendingWaiterLocked(lost);
                if (recoveryStopped)
                {
                    _recoveryStopped = true;
                    // A recovery that reaches the limit ends here, so the drive reads Faulted with
                    // this loss as its cause now, before the loss is reported, rather than when the
                    // recovery returns.
                    if (_recoveryState == RecoveryState.Recovering)
                    {
                        _recoveryState = RecoveryState.None;
                    }

                    _refusedStartFault = lost;
                    _watchFailureMessage = lost.Message;
                }

                NoteWatchStateLocked(new WatchFault(WatchFaultKind.CatchUpLost, drive, lost));
            }

            RaiseWatchStateChanged();
            return lost;
        }

        string DescribeLostCatchUp(int consecutiveLostCatchUps, bool recoveryStopped,
            JournalCheckpointLoss catchUpLoss)
        {
            if (!recoveryStopped)
            {
                return $"Drive {Drive}: the journal no longer held the cursor armed before the scan when the " +
                       $"scan finished, so its catch-up was lost ({consecutiveLostCatchUps} in a row). The drive is " +
                       "being scanned again.";
            }

            var remedy = catchUpLoss.SizeThatWouldHaveRetained is { } size
                ? $"Grow the drive's USN journal to at least {size} bytes (BrokerSession.GrowUsnJournalAsync), " +
                  "then call FileIndex.RescanAsync for this drive."
                : "Call FileIndex.RescanAsync for this drive.";
            return $"Drive {Drive}: {consecutiveLostCatchUps} scans in a row lost their journal catch-up, so " +
                   $"the drive keeps its last block, which cannot be watched. {remedy}";
        }
    }
}
