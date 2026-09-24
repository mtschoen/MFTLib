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
    ///     A rescan while watching disarms only this drive, swaps its block, resets catch-up to
    ///     <see cref="WatchCatchUpState.CatchingUp" />, and re-arms it from the fresh cursor.
    ///     A blockless drive is scanned and adopted rather than swapped; failed scans rewrite status
    ///     to <see cref="DriveFailureKind.ProducerFailed" />. Offline drives are refused.
    ///     Watch faults are tracked per drive: a successful re-arm clears this drive's outstanding
    ///     watch fault, so <see cref="StopWatchingAsync" /> no longer rethrows it, and leaves every
    ///     other drive's fault, subscriber faults, and source faults in place. If the re-arm fails,
    ///     the drive's earlier fault is restored unless a newer fault for it was recorded meanwhile.
    ///     <para>
    ///         A drive whose watch was already faulted when this rescan began is recovered only
    ///         after a replacement block is committed. If production returns no block, throws,
    ///         or is cancelled before replacement, its previous block, watch failure, faulted
    ///         catch-up, outstanding watch fault, and checkpoint-loss report remain in place.
    ///         The drive is neither re-armed from its old cursor nor used to restart an ended
    ///         watch session. Non-cancellation producer failures are available through
    ///         <see cref="DriveStatus.MftProducerFailureMessage" /> even when this task completes
    ///         normally. A previously healthy drive may instead resume its old watch after a
    ///         failed rescan. Other drives continue watching independently.
    ///     </para>
    ///     <para>
    ///         A drive a cache-only open adopted despite a lost journal checkpoint (see
    ///         <see cref="FileIndexOptions.InitialOpenCacheOnly" />) is never disarmed here, since
    ///         it was never armed. If the scan replaces its block, the fresh cursor is armed onto
    ///         whatever watch session is running by the time the scan finishes, even one that
    ///         started after this call began: the session captured at the start is a snapshot, not
    ///         a lock, so a session can appear while the scan is still in flight. If the scan
    ///         fails without producing a new block, the old, still-unresumable block is left in
    ///         place and the drive's watch refusal is left exactly as it was, rather than being
    ///         armed from a cursor the journal still cannot resume.
    ///     </para>
    ///     <para>
    ///         If the watch session ends while the rescan is in flight, because every watched
    ///         drive failed and the pump stopped reading, the drive is not armed onto that
    ///         session's released stream. After an eligible recovery, the rescan reclaims the ended session and starts a fresh
    ///         one in its place, unless the session ended through cancellation, and it never stops
    ///         a session that is still running to do so. That restart recovers only this drive: it
    ///         clears this drive's <see cref="DriveStatus.WatchFailureMessage" /> and faulted
    ///         <see cref="DriveStatus.WatchCatchUp" />, keeps every other drive's recorded failure,
    ///         faulted catch-up, and <see cref="DriveStatus.CheckpointLoss" />, and leaves every
    ///         drive with a recorded failure out of the new session. A drive whose live watch lost
    ///         its journal checkpoint is therefore never armed from a cursor the journal no longer
    ///         holds; each such drive needs its own rescan, which arms it onto the running session.
    ///         The ended session's outstanding faults, other drives', subscriber, and source faults
    ///         alike, are retained on the index until the next <see cref="StopWatchingAsync" />,
    ///         which rethrows the earliest of them even if the fresh session has since ended on its
    ///         own. A disarm or arm the source rejects with
    ///         <see cref="WatchStreamNotRunningException" /> after the session's stream started
    ///         means the stream was released while the pump was still finishing, so the rescan
    ///         waits for the pump, bounded by <paramref name="cancellationToken" />, and treats the
    ///         session as ended. <see cref="StartWatchingAsync" /> still clears every armed drive's
    ///         failure.
    ///     </para>
    ///     <para>
    ///         A rescan issued any time after <see cref="StartWatchingAsync" /> completes finds the
    ///         session's stream ready for its disarm and re-arm, because that start completes only
    ///         once the source reports readiness. A session found still starting, whether because
    ///         the start has not been awaited or because it began while this rescan's scan ran, is
    ///         waited for before its source is asked to disarm or arm, bounded by
    ///         <paramref name="cancellationToken" />; a rescan cancelled during that wait has touched
    ///         nothing and records no failure against the drive. If that start fails or is cancelled instead, it
    ///         is the start that reports the failure and releases the session: this rescan neither
    ///         disarms nor arms anything on it and starts no session in its place.
    ///     </para>
    /// </remarks>
    public async Task RescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_driveConfigurations.TryGetValue(char.ToUpperInvariant(driveLetter), out var drive))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        await _rescanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await WaitForCurrentWatchSessionToStartAsync(cancellationToken).ConfigureAwait(false);
            var requiresReplacement = RequiresReplacementForWatchRecovery(driveLetter);

            // Disarming before the gate, never under it: the pump takes _swapGate synchronously
            // inside ApplyJournalEntriesCore, so touching the watch while this method holds the gate
            // would deadlock the rescan against its own pump.
            SuspendedWatch suspended;
            try
            {
                suspended = await SuspendDriveForRescanAsync(driveLetter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception suspendFailure)
            {
                RecordWatchFailure(driveLetter, suspendFailure);
                throw;
            }

            BlockReplacementOutcome replacement;
            try
            {
                replacement = await SwapDriveBlockAsync(drive, driveLetter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception swapFailure)
            {
                await ResumeAfterFailedSwapAsync(driveLetter, suspended, requiresReplacement,
                    swapFailure, cancellationToken).ConfigureAwait(false);
                throw;
            }

            try
            {
                await ResumeDriveAfterRescanAsync(driveLetter, suspended, replacement,
                    requiresReplacement, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception resumeFailure)
            {
                RecordWatchFailure(driveLetter, resumeFailure);
                throw;
            }
        }
        finally
        {
            _rescanGate.Release();
        }
    }

    enum BlockReplacementOutcome
    {
        NotReplaced,
        Replaced
    }

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
               _cacheOnlyUnresumableCheckpointOrdinals.Contains(driveOrdinal);
    }

    async Task<BlockReplacementOutcome> SwapDriveBlockAsync(IndexedDrive drive, char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetDriveOrdinal(driveLetter, out var driveOrdinal))
        {
            return await ScanBlocklessDriveAsync(drive, driveLetter, cancellationToken).ConfigureAwait(false);
        }

        DriveBlock superseded;
        CacheSlotState previousCacheSlot;
        JournalCheckpointLoss? previousCheckpointLoss;
        bool previouslyUnresumable;
        lock (_stateLock)
        {
            superseded = _driveBlocks[driveOrdinal];
            previousCacheSlot = _cacheSlotsByOrdinal.GetValueOrDefault(driveOrdinal);
            previousCheckpointLoss = _checkpointLossesByOrdinal.GetValueOrDefault(driveOrdinal);
            previouslyUnresumable = _cacheOnlyUnresumableCheckpointOrdinals.Contains(driveOrdinal);
        }

        // The rename-aside is licensed by the owner lock: an index that does not hold it (its
        // block came from a private scan while another index owned the slot) rescans privately
        // again and never touches the canonical file.
        var ownsCanonicalSlot = !_options.NoCache && EnsureCanonicalOwnership(drive);
        var target = ComputeScanTarget(drive, ownsCanonicalSlot);
        var retiredPath = target.OwnsCanonicalSlot
            ? RenameAsideForRescan(target.Path, superseded, _options.Diagnostics)
            : null;
        var scanResult = await ProduceRescannedBlockAsync(drive, driveOrdinal, target, retiredPath,
            superseded, cancellationToken).ConfigureAwait(false);
        if (scanResult is not { } completedScan)
        {
            return BlockReplacementOutcome.NotReplaced;
        }

        try
        {
            await CommitBlockUnderSwapGateAsync(
                () =>
                {
                    _driveBlocks[driveOrdinal] = completedScan.DriveBlock;
                    _cacheSlotsByOrdinal[driveOrdinal] = DescribeCacheSlot(target.OwnsCanonicalSlot);
                    _blockSourcesByOrdinal[driveOrdinal] = BlockSource.ProducedByScan;
                    _discardedBlocksByOrdinal.Remove(driveOrdinal);
                    // All three explain how the block being replaced came to be (or, for the
                    // last one, that it could not be watched), so all three stop applying the
                    // moment a new block with a fresh cursor takes its place.
                    _checkpointLossesByOrdinal.Remove(driveOrdinal);
                    _cacheOnlyUnresumableCheckpointOrdinals.Remove(driveOrdinal);
                    _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = completedScan.AccessDeniedSubtreeCount;
                },
                () =>
                {
                    if (ReferenceEquals(_driveBlocks[driveOrdinal], completedScan.DriveBlock))
                    {
                        _driveBlocks[driveOrdinal] = superseded;
                        _cacheSlotsByOrdinal[driveOrdinal] = previousCacheSlot;
                        if (previousCheckpointLoss is not null)
                        {
                            _checkpointLossesByOrdinal[driveOrdinal] = previousCheckpointLoss;
                        }

                        if (previouslyUnresumable)
                        {
                            _cacheOnlyUnresumableCheckpointOrdinals.Add(driveOrdinal);
                        }
                    }
                },
                completedScan.DriveBlock, cancellationToken).ConfigureAwait(false);
            return BlockReplacementOutcome.Replaced;
        }
        catch
        {
            if (retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, target.Path, superseded, _options.Diagnostics);
            }

            throw;
        }
    }

    (DriveStatus Blockless, ushort DriveOrdinal) GetBlocklessDriveForRescan(char driveLetter)
    {
        lock (_stateLock)
        {
            var index = FindBlocklessStatusIndexLocked(driveLetter);
            if (index < 0 || _blocklessDriveStatuses[index].State != DriveState.Failed)
            {
                throw new ArgumentException($"Drive {driveLetter} has no block.", nameof(driveLetter));
            }

            return (_blocklessDriveStatuses[index], (ushort)_driveBlocks.Count);
        }
    }

    void RecordBlocklessProducerFailure(char driveLetter, ushort driveOrdinal)
    {
        lock (_stateLock)
        {
            var message = _mftProducerFailureMessagesByOrdinal.GetValueOrDefault(driveOrdinal);
            _mftProducerFailureMessagesByOrdinal.Remove(driveOrdinal);
            var index = FindBlocklessStatusIndexLocked(driveLetter);
            if (index >= 0)
            {
                _blocklessDriveStatuses[index] = _blocklessDriveStatuses[index] with
                {
                    MftProducerFailureMessage = message,
                    FailureKind = DriveFailureKind.ProducerFailed
                };
            }
        }
    }

    async Task CommitBlockUnderSwapGateAsync(Action commit, Action rollback, DriveBlock driveBlock,
        CancellationToken cancellationToken)
    {
        try
        {
            await _swapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_stateLock)
                {
                    commit();
                }

                PublishSnapshot();
            }
            finally
            {
                _swapGate.Release();
            }
        }
        catch
        {
            lock (_stateLock)
            {
                rollback();
            }

            driveBlock.Block.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     The rescan of a drive that has no block: one a cache-only open declined or whose
    ///     producer failed. Scans and adopts rather than swapping.
    /// </summary>
    async Task<BlockReplacementOutcome> ScanBlocklessDriveAsync(IndexedDrive drive, char driveLetter, CancellationToken cancellationToken)
    {
        var (blockless, driveOrdinal) = GetBlocklessDriveForRescan(driveLetter);
        var ownsCanonicalSlot = !_options.NoCache && EnsureCanonicalOwnership(drive);
        var target = ComputeScanTarget(drive, ownsCanonicalSlot);
        var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, target.Path,
            target.DeleteOnClose, cancellationToken).ConfigureAwait(false);
        if (scanResult is not { } completedScan)
        {
            RecordBlocklessProducerFailure(driveLetter, driveOrdinal);
            ReleaseCanonicalOwnership(driveLetter);
            return BlockReplacementOutcome.NotReplaced;
        }

        await CommitBlockUnderSwapGateAsync(
            () =>
            {
                _driveBlocks.Add(completedScan.DriveBlock);
                _cacheSlotsByOrdinal[driveOrdinal] = DescribeCacheSlot(target.OwnsCanonicalSlot);
                _blockSourcesByOrdinal[driveOrdinal] = BlockSource.ProducedByScan;
                _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = completedScan.AccessDeniedSubtreeCount;
                var index = FindBlocklessStatusIndexLocked(driveLetter);
                if (index >= 0)
                {
                    _blocklessDriveStatuses.RemoveAt(index);
                }
            },
            () =>
            {
                if (_driveBlocks.Count > driveOrdinal &&
                    ReferenceEquals(_driveBlocks[driveOrdinal], completedScan.DriveBlock))
                {
                    _driveBlocks.RemoveAt(driveOrdinal);
                }

                _blockSourcesByOrdinal.Remove(driveOrdinal);
                _cacheSlotsByOrdinal.Remove(driveOrdinal);
                _accessDeniedSubtreeCountByOrdinal.Remove(driveOrdinal);
                if (FindBlocklessStatusIndexLocked(driveLetter) < 0)
                {
                    _blocklessDriveStatuses.Add(blockless);
                }
            },
            completedScan.DriveBlock, cancellationToken).ConfigureAwait(false);
        return BlockReplacementOutcome.Replaced;
    }

    int FindBlocklessStatusIndexLocked(char driveLetter) =>
        _blocklessDriveStatuses.FindIndex(status =>
            char.ToUpperInvariant(status.DriveLetter) == char.ToUpperInvariant(driveLetter));

    /// <summary>
    ///     Runs the producer for a rescan and restores the renamed-aside cache file whenever the
    ///     scan fails, is cancelled, or reports this drive failed. Null means nothing to swap.
    /// </summary>
    async Task<ScanDriveResult?> ProduceRescannedBlockAsync(IndexedDrive drive, ushort driveOrdinal,
        ScanBlockTarget target, string? retiredPath, DriveBlock superseded,
        CancellationToken cancellationToken)
    {
        try
        {
            var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, target.Path, target.DeleteOnClose,
                cancellationToken).ConfigureAwait(false);
            if (scanResult is null && retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, target.Path, superseded, _options.Diagnostics);
            }

            return scanResult;
        }
        catch
        {
            if (retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, target.Path, superseded, _options.Diagnostics);
            }

            throw;
        }
    }
}
