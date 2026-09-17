namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Rebuilds one drive's block into a new file and swaps it into the current snapshot.
    ///     For a cache-mode drive, the file at the canonical path is renamed aside first (safe
    ///     even while it is still mapped, since <see cref="BlockFile" /> opens with
    ///     <see cref="FileShare.Delete" />) so the new block can take the canonical name while a
    ///     handle from the retired snapshot keeps reading the renamed file until that snapshot is
    ///     released. If the scan fails or is cancelled, the previous in-memory block is left in
    ///     place and the renamed-aside file is moved straight back to restore the on-disk cache.
    /// </summary>
    /// <remarks>
    ///     A rescan while watching disarms only this drive, swaps its block, resets catch-up to
    ///     <see cref="WatchCatchUpState.CatchingUp" />, and re-arms it from the fresh cursor.
    ///     A blockless drive is scanned and adopted rather than swapped; failed scans rewrite status
    ///     to <see cref="DriveFailureKind.ProducerFailed" />. Offline drives are refused.
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

            try
            {
                await SwapDriveBlockAsync(drive, driveLetter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception swapFailure)
            {
                await ResumeAfterFailedSwapAsync(driveLetter, suspended, swapFailure, cancellationToken).ConfigureAwait(false);
                throw;
            }

            try
            {
                await ResumeDriveAfterRescanAsync(driveLetter, suspended, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     Puts the drive back on the watch after a swap that failed, so the caller's exception is
    ///     the only consequence.
    /// </summary>
    async Task ResumeAfterFailedSwapAsync(char driveLetter, SuspendedWatch suspended,
        Exception swapFailure, CancellationToken cancellationToken)
    {
        try
        {
            await ResumeDriveAfterRescanAsync(driveLetter, suspended, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception resumeFailure)
        {
            RecordWatchFailure(driveLetter, swapFailure);
            throw new AggregateException(
                $"Drive {driveLetter} could not be re-armed after its rescan failed, so its watch is stopped.", swapFailure, resumeFailure);
        }
    }

    async Task SwapDriveBlockAsync(IndexedDrive drive, char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetDriveOrdinal(driveLetter, out var driveOrdinal))
        {
            await ScanBlocklessDriveAsync(drive, driveLetter, cancellationToken).ConfigureAwait(false);
            return;
        }

        DriveBlock superseded;
        lock (_stateLock)
        {
            superseded = _driveBlocks[driveOrdinal];
        }

        var blockPath = ComputeScanBlockPath(drive);
        var retiredPath = _options.NoCache ? null : RenameAsideForRescan(blockPath, superseded);
        var scanResult = await ProduceRescannedBlockAsync(drive, driveOrdinal, blockPath, retiredPath,
            superseded, cancellationToken).ConfigureAwait(false);
        if (scanResult is not { } completedScan)
        {
            return;
        }

        try
        {
            await CommitBlockUnderSwapGateAsync(
                () =>
                {
                    _driveBlocks[driveOrdinal] = completedScan.DriveBlock;
                    _blockSourcesByOrdinal[driveOrdinal] = BlockSource.ProducedByScan;
                    _discardedBlocksByOrdinal.Remove(driveOrdinal);
                    _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = completedScan.AccessDeniedSubtreeCount;
                },
                () =>
                {
                    if (ReferenceEquals(_driveBlocks[driveOrdinal], completedScan.DriveBlock))
                    {
                        _driveBlocks[driveOrdinal] = superseded;
                    }
                },
                completedScan.DriveBlock, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, blockPath, superseded);
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
    async Task ScanBlocklessDriveAsync(IndexedDrive drive, char driveLetter, CancellationToken cancellationToken)
    {
        var (blockless, driveOrdinal) = GetBlocklessDriveForRescan(driveLetter);
        var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, ComputeScanBlockPath(drive),
            cancellationToken).ConfigureAwait(false);
        if (scanResult is not { } completedScan)
        {
            RecordBlocklessProducerFailure(driveLetter, driveOrdinal);
            return;
        }

        await CommitBlockUnderSwapGateAsync(
            () =>
            {
                _driveBlocks.Add(completedScan.DriveBlock);
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
                _accessDeniedSubtreeCountByOrdinal.Remove(driveOrdinal);
                if (FindBlocklessStatusIndexLocked(driveLetter) < 0)
                {
                    _blocklessDriveStatuses.Add(blockless);
                }
            },
            completedScan.DriveBlock, cancellationToken).ConfigureAwait(false);
    }

    int FindBlocklessStatusIndexLocked(char driveLetter) =>
        _blocklessDriveStatuses.FindIndex(status =>
            char.ToUpperInvariant(status.DriveLetter) == char.ToUpperInvariant(driveLetter));

    /// <summary>
    ///     Runs the producer for a rescan and restores the renamed-aside cache file whenever the
    ///     scan fails, is cancelled, or reports this drive failed. Null means nothing to swap.
    /// </summary>
    async Task<ScanDriveResult?> ProduceRescannedBlockAsync(IndexedDrive drive, ushort driveOrdinal,
        string blockPath, string? retiredPath, DriveBlock superseded, CancellationToken cancellationToken)
    {
        try
        {
            var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, blockPath, cancellationToken)
                .ConfigureAwait(false);
            if (scanResult is null && retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, blockPath, superseded);
            }

            return scanResult;
        }
        catch
        {
            if (retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, blockPath, superseded);
            }

            throw;
        }
    }

    /// <summary>
    ///     Stops the rescanned drive at the watch source before the gate is taken.
    /// </summary>
    async Task<SuspendedWatch> SuspendDriveForRescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        WatchSession? session;
        lock (_stateLock)
        {
            session = _watchSession;
        }

        if (session is null)
        {
            return default;
        }

        if (session.Pump.IsCompleted)
        {
            var sessionToken = session.CallerToken;
            try
            {
                await StopWatchingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }

            return new SuspendedWatch(null, RestartWholeSession: true, sessionToken);
        }

        if (session.ContainsTarget(driveLetter))
        {
            await session.Source.DisarmDriveAsync(driveLetter, cancellationToken).ConfigureAwait(false);
        }

        return new SuspendedWatch(session, RestartWholeSession: false, session.CallerToken);
    }

    /// <summary>
    ///     Puts the rescanned drive back on the watch it was taken off.
    /// </summary>
    async Task ResumeDriveAfterRescanAsync(char driveLetter, SuspendedWatch suspended,
        CancellationToken cancellationToken)
    {
        if (suspended.Session is { } session)
        {
            lock (_stateLock)
            {
                if (!TryGetDriveOrdinalLocked(driveLetter, out _))
                {
                    return;
                }
            }

            var target = BuildWatchTarget(driveLetter);
            session.RegisterTarget(target);
            ClearWatchFailure(driveLetter);
            ArmWatchCatchUp(driveLetter);
            await session.Source.ArmDriveAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }

        ClearWatchFailure(driveLetter);
        RemoveStaleFaultedCatchUp(driveLetter);
        if (suspended.RestartWholeSession)
        {
            await StartWatchingCoreAsync(suspended.SessionToken, cancellationToken).ConfigureAwait(false);
        }
    }

    void ClearWatchFailure(char driveLetter)
    {
        lock (_stateLock)
        {
            ClearWatchFailureLocked(driveLetter);
        }
    }

    /// <summary>
    ///     Renames the file currently at <paramref name="canonicalPath" /> aside, if one exists,
    ///     and schedules it for deletion once <paramref name="superseded" /> is fully released.
    /// </summary>
    static string? RenameAsideForRescan(string canonicalPath, DriveBlock superseded)
    {
        if (!File.Exists(canonicalPath))
        {
            return null;
        }

        var retiredPath = $"{canonicalPath}.retired-{Guid.NewGuid():N}";
        File.Move(canonicalPath, retiredPath);
        superseded.ScheduleDeleteAt(retiredPath);
        return retiredPath;
    }

    /// <summary>
    ///     Undoes <see cref="RenameAsideForRescan" /> when the scan fails or is cancelled.
    /// </summary>
    static void RestoreRetiredFile(string retiredPath, string canonicalPath, DriveBlock superseded)
    {
        try
        {
            if (File.Exists(retiredPath))
            {
                if (File.Exists(canonicalPath))
                {
                    File.Delete(canonicalPath);
                }

                File.Move(retiredPath, canonicalPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        superseded.ClearScheduledDelete();
    }

    /// <summary>Swaps in a new snapshot over the current <see cref="_driveBlocks" /> list.</summary>
    void PublishSnapshot()
    {
        Snapshot previous;
        lock (_stateLock)
        {
            previous = _snapshot ?? throw new ObjectDisposedException(nameof(FileIndex));
            _snapshot = Snapshot.Create(_driveBlocks);
        }

        _retiredSnapshots.RemoveAll(retired => retired.Release.IsReleaseComplete);
        _retiredSnapshots.Add(new RetiredSnapshot(previous));
    }

    /// <summary>Forces every retained release state to release its blocks now.</summary>
    async ValueTask ReleaseAllRetiredSnapshotsAsync()
    {
        foreach (var retired in _retiredSnapshots)
        {
            await retired.Release.ReleaseAsync().ConfigureAwait(false);
        }

        _retiredSnapshots.Clear();
    }

    /// <summary>What a rescan took off the watch, and what it therefore has to put back.</summary>
    readonly record struct SuspendedWatch(WatchSession? Session, bool RestartWholeSession,
        CancellationToken SessionToken);
}
