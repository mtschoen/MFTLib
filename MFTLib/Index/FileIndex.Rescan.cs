namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Rebuilds one drive's block into a new file and swaps it into the current snapshot.
    ///     For a cache-mode drive, the file at the canonical path is renamed aside first (safe
    ///     even while it is still mapped, since <see cref="BlockFile" /> opens with
    ///     <see cref="FileShare.Delete" />) so the new block can take the canonical name while a
    ///     handle from the retired snapshot keeps reading the renamed file until that snapshot is
    ///     released. The rename only happens while this index holds the canonical path's owner
    ///     lock; a block another index owns is never renamed. If the scan fails or is cancelled,
    ///     the previous in-memory block is left in place and the renamed-aside file is moved
    ///     straight back to restore the on-disk cache.
    /// </summary>
    /// <remarks>
    ///     The rescan holds the drive's lifecycle gate from entry to exit, so a
    ///     <see cref="StartWatchingAsync(char, CancellationToken)" /> of the same drive waits for it, and the canonical
    ///     file's rename-aside, the scan into the canonical path, and the restore on failure are
    ///     serialized per drive. Nothing else serializes it: rescans of different drives produce at
    ///     the same time, and each commits under its own drive's write gate. A blockless drive is
    ///     scanned and adopted rather than swapped; a failed scan of one rewrites its status to
    ///     <see cref="DriveFailureKind.ProducerFailed" />. Offline drives are refused.
    ///     <para>
    ///         A drive that is watching has its watch stopped, and its teardown awaited, before the
    ///         scan runs, so nothing the old watch reads can reach the new block. After a committed
    ///         replacement the drive's watch is started again from the new block's cursor if it is
    ///         still requested, which a <see cref="StopWatchingAsync(char, CancellationToken)" /> during the rescan clears.
    ///         That restart replaces a faulted watch, clearing its
    ///         <see cref="DriveStatus.WatchFailureMessage" /> and faulted
    ///         <see cref="DriveStatus.WatchCatchUp" />. A replacement also clears a refused start's
    ///         failure, including the refusal of a block whose checkpoint could not be resumed.
    ///         Other drives are never touched. A rescan supersedes the drive's queued automatic
    ///         recovery, which then ends without scanning.
    ///     </para>
    ///     <para>
    ///         A scan whose journal catch-up was lost publishes its block, raises
    ///         <see cref="WatchFaultKind.CatchUpLost" />, and scans the drive again at once while
    ///         this rescan still holds the lifecycle gate, until a scan's catch-up holds or the
    ///         drive's <see cref="DriveStatus.ConsecutiveLostCatchUps" /> reaches
    ///         <see cref="LostCatchUpRecoveryLimit" />; then this throws the last
    ///         <see cref="JournalCatchUpLostException" />, and the drive keeps its last block,
    ///         queryable but not watchable. With the count already at the limit it makes exactly one
    ///         attempt.
    ///     </para>
    ///     <para>
    ///         If the scan produces no replacement, a drive whose watch was healthy restarts its
    ///         watch from its old cursor (when still requested). A drive whose watch had faulted,
    ///         whose start was refused, or that has no block stays exactly as it was: its previous
    ///         block, watch failure, faulted catch-up, outstanding watch fault, and checkpoint-loss
    ///         report remain, and it is not restarted from a cursor that failure condemns. A
    ///         producer that returned no block fails this task with
    ///         <see cref="InvalidOperationException" /> carrying the producer's failure, which
    ///         <see cref="DriveStatus.MftProducerFailureMessage" /> also reports.
    ///     </para>
    ///     <para>
    ///         <paramref name="cancellationToken" /> is linked to the index's disposal, so
    ///         disposing the index cancels a rescan in flight, including one that has published
    ///         its block and has not yet restarted the watch. A restart after the scan is not
    ///         bounded by that token: the token cancels the rescan, not the drive's watch, which a
    ///         stop or disposal ends instead. The wait for the old watch's teardown does not observe
    ///         the token either; a cancellation is observed by the scan, and a cancelled scan
    ///         follows the failed-scan rule above, so a healthy watch restarts from its old cursor.
    ///     </para>
    /// </remarks>
    public async Task RescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        if (RejectInsideHandler(nameof(RescanAsync)) is { } rejection)
        {
            throw rejection;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_driveConfigurations.TryGetValue(char.ToUpperInvariant(driveLetter), out var drive))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        var runtime = GetDriveRuntime(driveLetter);
        using var rescanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposalToken);
        await runtime.LifecycleGate.WaitAsync(rescanCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfCancelledByDisposal(rescanCancellation.Token);
            await RescanWithGateHeldAsync(runtime, drive, recovery: null, rescanCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            runtime.LifecycleGate.Release();
        }
    }

    /// <summary>What one attempt of a scan operation did.</summary>
    /// <param name="Published">False when the producer returned no block, so nothing was published.</param>
    /// <param name="ProducerFailure">The producer's failure when it returned no block.</param>
    /// <param name="ProducerFailureMessage">What the drive's status reports for that failure.</param>
    /// <param name="CatchUpLoss">Set when the published block's catch-up was lost.</param>
    /// <param name="ConsecutiveLostCatchUps">The drive's count after a publish.</param>
    readonly record struct ScanAttempt(
        bool Published,
        Exception? ProducerFailure = null,
        string? ProducerFailureMessage = null,
        JournalCheckpointLoss? CatchUpLoss = null,
        int ConsecutiveLostCatchUps = 0);

    bool RequiresReplacementForWatchRecovery(char driveLetter)
    {
        lock (_stateLock)
        {
            return RequiresReplacementForWatchRecoveryLocked(driveLetter);
        }
    }

    bool RequiresReplacementForWatchRecoveryLocked(char driveLetter)
    {
        return !TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal) ||
               _watchFailureMessagesByOrdinal.ContainsKey(driveOrdinal) ||
               _unresumableCheckpointsByOrdinal.ContainsKey(driveOrdinal);
    }

    /// <summary>
    ///     One attempt of a scan operation: produces a block for the drive and publishes it, or,
    ///     when the producer returns none, records the producer's failure against the drive and
    ///     publishes nothing. A drive with a block has its canonical file renamed aside first and
    ///     restored whenever nothing replaces it.
    /// </summary>
    async Task<ScanAttempt> ScanAndPublishAsync(DriveRuntime runtime, IndexedDrive drive, bool clearsCheckpointLoss,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelledByDisposal(cancellationToken);
        var superseded = FindBlockForRescan(runtime.DriveLetter);

        // The rename-aside is licensed by the owner lock: an index that does not hold it (its
        // block came from a private scan while another index owned the slot) rescans privately
        // again and never touches the canonical file.
        var ownsCanonicalSlot = !_options.NoCache && EnsureCanonicalOwnership(drive);
        var target = ComputeScanTarget(drive, ownsCanonicalSlot);
        var retired = RenameAsideForRescan(target, superseded);

        PendingDriveResult produced;
        try
        {
            produced = await ProduceDriveBlockAsync(drive, target.Path, target.DeleteOnClose, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            RestoreRetiredFile(retired);
            throw;
        }

        if (produced.Block is not { } block)
        {
            RestoreRetiredFile(retired);
            RecordRescanProducerFailure(runtime.DriveLetter, produced.ProducerFailureMessage);
            return new ScanAttempt(Published: false, produced.ProducerFailure, produced.ProducerFailureMessage);
        }

        try
        {
            var consecutiveLostCatchUps = await PublishRescannedBlockAsync(runtime, drive, block,
                produced with { CacheSlot = DescribeCacheSlot(target.OwnsCanonicalSlot) }, clearsCheckpointLoss,
                cancellationToken).ConfigureAwait(false);
            return new ScanAttempt(Published: true, CatchUpLoss: produced.CatchUpLoss,
                ConsecutiveLostCatchUps: consecutiveLostCatchUps);
        }
        catch
        {
            // Never published, so it owns no references; its mapping is closed directly.
            block.Dispose();
            RestoreRetiredFile(retired);
            throw;
        }
    }

    /// <summary>
    ///     The drive's published block, or null for a drive that has none because a cache-only
    ///     open declined it or its producer failed. An offline drive cannot be rescanned.
    /// </summary>
    DriveBlock? FindBlockForRescan(char driveLetter)
    {
        lock (_stateLock)
        {
            if (TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal))
            {
                return _driveBlocks[driveOrdinal];
            }

            var index = FindBlocklessStatusIndexLocked(driveLetter);
            if (index < 0 || _blocklessDriveStatuses[index].State != DriveState.Failed)
            {
                throw new ArgumentException($"Drive {driveLetter} has no block.", nameof(driveLetter));
            }

            return null;
        }
    }

    /// <summary>
    ///     Records a rescan's producer failure: against the published block's ordinal when the
    ///     drive has one, and otherwise on the drive's blockless status, keyed by letter, which
    ///     then reads <see cref="DriveFailureKind.ProducerFailed" />.
    /// </summary>
    void RecordRescanProducerFailure(char driveLetter, string? message, bool endsOpenSettle = false)
    {
        lock (_stateLock)
        {
            if (endsOpenSettle)
            {
                ClaimSettledCountLocked(driveLetter);
            }

            if (TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal))
            {
                if (message is null)
                {
                    _mftProducerFailureMessagesByOrdinal.Remove(driveOrdinal);
                }
                else
                {
                    _mftProducerFailureMessagesByOrdinal[driveOrdinal] = message;
                }

                return;
            }

            var index = FindBlocklessStatusIndexLocked(driveLetter);
            if (index >= 0)
            {
                _blocklessDriveStatuses[index] = _blocklessDriveStatuses[index] with
                {
                    MftProducerFailureMessage = message,
                    FailureKind = DriveFailureKind.ProducerFailed
                };
            }

            ReleaseCanonicalOwnershipLocked(driveLetter);
        }
    }

    int FindBlocklessStatusIndexLocked(char driveLetter) =>
        _blocklessDriveStatuses.FindIndex(status =>
            char.ToUpperInvariant(status.DriveLetter) == char.ToUpperInvariant(driveLetter));
}
