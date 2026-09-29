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
    ///     <see cref="StartWatchingAsync" /> of the same drive waits for it, and the canonical
    ///     file's rename-aside, the scan into the canonical path, and the restore on failure are
    ///     serialized per drive. A blockless drive is scanned and adopted rather than swapped;
    ///     failed scans rewrite status to <see cref="DriveFailureKind.ProducerFailed" />. Offline
    ///     drives are refused.
    ///     <para>
    ///         A drive that is watching has its watch stopped, and its teardown awaited, before the
    ///         scan runs, so nothing the old watch reads can reach the new block. After a committed
    ///         replacement the drive's watch is started again from the new block's cursor if it is
    ///         still requested, which a <see cref="StopWatchingAsync" /> during the rescan clears.
    ///         That restart replaces a faulted watch, clearing its
    ///         <see cref="DriveStatus.WatchFailureMessage" /> and faulted
    ///         <see cref="DriveStatus.WatchCatchUp" />. A replacement also clears a refused start's
    ///         failure, including the refusal of a block a cache-only open adopted despite a lost
    ///         journal checkpoint. Other drives are never touched.
    ///     </para>
    ///     <para>
    ///         If the scan produces no replacement, a drive whose watch was healthy restarts its
    ///         watch from its old cursor (when still requested). A drive whose watch had faulted,
    ///         whose start was refused, or that has no block stays exactly as it was: its previous
    ///         block, watch failure, faulted catch-up, outstanding watch fault, and checkpoint-loss
    ///         report remain, and it is not restarted from a cursor that failure condemns.
    ///         Non-cancellation producer failures are available through
    ///         <see cref="DriveStatus.MftProducerFailureMessage" /> even when this task completes
    ///         normally.
    ///     </para>
    ///     <para>
    ///         A restart after the scan is not bounded by <paramref name="cancellationToken" />:
    ///         the token cancels the rescan, not the drive's watch, which a stop or disposal ends
    ///         instead. Cancelling the token while the old watch's teardown is awaited throws before
    ///         the scan starts and leaves the watch stopped but still requested, so the next start
    ///         or rescan of the drive starts it again.
    ///     </para>
    /// </remarks>
    public async Task RescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_driveConfigurations.TryGetValue(char.ToUpperInvariant(driveLetter), out var drive))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        var runtime = GetDriveRuntime(driveLetter);
        await runtime.LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rescanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await RescanWithGatesHeldAsync(runtime, drive, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _rescanGate.Release();
            }
        }
        finally
        {
            runtime.LifecycleGate.Release();
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
