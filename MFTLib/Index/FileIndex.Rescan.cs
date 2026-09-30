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
    ///         A healthy watch keeps running throughout production. Publication creates the new
    ///         snapshot, then retires the old watch and commits the replacement together under the
    ///         drive's write gate and state lock. Pending catch-up waits are cancelled at that
    ///         retirement. The old pump and any predecessor are drained after releasing both locks,
    ///         with only the lifecycle gate held. A faulted current watch stays until restart
    ///         registration supersedes it. The replacement watch starts from the new block's cursor
    ///         when still requested; a stop during the rescan clears that request.
    ///         An outstanding subscriber fault survives retirement and teardown for that stop to
    ///         rethrow once, until the replacement handle is published or its start fails with
    ///         <see cref="WatchFaultKind.RescanRestart" />, which supersedes the old fault.
    ///         Other drives are untouched. A committed manual rescan supersedes recovery queued
    ///         for the replaced block and clears its recovery state in the publication lock;
    ///         a watch fault during failed production can still recover.
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
    ///         If the scan produces no replacement, its failure or cancellation leaves the watch
    ///         instance and its catch-up waits untouched. Journal changes applied during production
    ///         remain on the old block, including when its renamed file is restored. A producer
    ///         returning no block fails this task with <see cref="InvalidOperationException" />
    ///         carrying its failure, also reported by <see cref="DriveStatus.MftProducerFailureMessage" />.
    ///         A failed retry after publishing a lost catch-up keeps that unresumable block and
    ///         refuses its watch.
    ///     </para>
    ///     <para>
    ///         A successful scan returns normally if its watch source fails to start the replacement
    ///         watch. <see cref="WatchFaultKind.RescanRestart" /> is raised once, with the start
    ///         failure as the inner exception and a message explaining that the rescan replaced the
    ///         block but could not start its watch. <see cref="DriveStatus.WatchFailureMessage" />
    ///         carries that message; the drive stays <see cref="WatchCatchUpState.Faulted" /> until
    ///         a consumer starts or rescans it. No automatic recovery is queued. Stop rethrows that
    ///         outstanding fault once. An automatic recovery's failed restart reports
    ///         <see cref="WatchFaultKind.Recovery" />.
    ///     </para>
    ///     <para>
    ///         A change applied before publication can be delivered afterwards, and the replacement
    ///         watch can replay it during catch-up. Consumers must tolerate repeated
    ///         <see cref="Changed" /> events across a successful swap. Queries may lag the old
    ///         watch's last updates until the new watch reports <see cref="WatchCatchUpState.CaughtUp" />.
    ///     </para>
    ///     <para>
    ///         <paramref name="cancellationToken" /> is linked to the index's disposal, so
    ///         disposing the index cancels a rescan in flight, including one that has published
    ///         its block and has not yet restarted the watch. A restart after the scan is not
    ///         bounded by that token: the token cancels the rescan, not the drive's watch, which a
    ///         stop or disposal ends instead. The commit-time teardown also ignores that token:
    ///         after publication the replacement is owned by the index and its handoff finishes.
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

    /// <summary>
    ///     One attempt of a scan operation: produces a block for the drive and publishes it, or,
    ///     when the producer returns none, records the producer's failure against the drive and
    ///     publishes nothing. A drive with a block has its canonical file renamed aside first and
    ///     restored whenever nothing replaces it.
    /// </summary>
    async Task<ScanAttempt> ScanAndPublishAsync(DriveRuntime runtime, IndexedDrive drive, bool clearsCheckpointLoss,
        RecoveryTicket? recovery, CancellationToken cancellationToken)
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
            DiscardUnproducedTarget(drive, target, retired);
            throw;
        }

        if (produced.Block is not { } block)
        {
            DiscardUnproducedTarget(drive, target, retired);
            RecordRescanProducerFailure(runtime.DriveLetter, produced.ProducerFailureMessage);
            return new ScanAttempt(Published: false, produced.ProducerFailure, produced.ProducerFailureMessage);
        }

        (int ConsecutiveLostCatchUps, WatchHandoff Handoff) publication;
        try
        {
            publication = await PublishRescannedBlockAsync(runtime, block,
                produced with { CacheSlot = DescribeCacheSlot(target.OwnsCanonicalSlot) }, clearsCheckpointLoss,
                recovery, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never published, so it owns no references; its mapping is closed directly.
            block.Dispose();
            RestoreRetiredFile(retired);
            throw;
        }

        // Ownership has passed to the snapshot. Teardown cannot enter the unpublished-block cleanup.
        await publication.Handoff.DrainAsync().ConfigureAwait(false);
        return new ScanAttempt(Published: true, CatchUpLoss: produced.CatchUpLoss,
            ConsecutiveLostCatchUps: publication.ConsecutiveLostCatchUps);
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
