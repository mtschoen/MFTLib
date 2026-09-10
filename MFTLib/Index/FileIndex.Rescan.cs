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
    ///     place (nothing here mutates <see cref="_driveBlocks" /> or publishes a new snapshot
    ///     until the scan succeeds), and the renamed-aside file is moved straight back to the
    ///     canonical path so the on-disk cache is restored too: a failed rescan attempt never
    ///     costs the drive its last good warm-start cache.
    /// </summary>
    /// <remarks>
    ///     A rescan while a watch is running disarms only this drive, swaps its block, clears its
    ///     <see cref="DriveStatus.WatchFailureMessage" />, and re-arms only this drive from the
    ///     fresh header cursor, while every other drive keeps streaming without losing a batch.
    ///     Nothing produced for this drive before the re-arm is applied to the new block, whether
    ///     it was still on the wire or already queued on the merged stream. A rescan issued after
    ///     every drive has already faulted reclaims the session and starts a fresh one over every
    ///     drive, discarding the fault it is recovering from. A failure to disarm or to re-arm
    ///     surfaces from here, and records the drive's
    ///     <see cref="DriveStatus.WatchFailureMessage" /> first, because either one leaves the
    ///     drive off the watch with nothing coming to put it back.
    /// </remarks>
    public async Task RescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_driveConfigurations.TryGetValue(char.ToUpperInvariant(driveLetter), out var drive))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        // Disarming before the gate, never under it: the pump takes _swapGate synchronously
        // inside ApplyJournalEntriesCore, so touching the watch while this method holds the gate
        // would deadlock the rescan against its own pump. Nothing here deadlocks the other way
        // either, because the disarm awaits a reader that never takes the gate and the merged
        // channel is unbounded, so a pump blocked on the gate never blocks a reader's write.
        SuspendedWatch suspended;
        try
        {
            suspended = await SuspendDriveForRescanAsync(driveLetter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception suspendFailure)
        {
            // A disarm that throws has already stopped the drive at the source, so leaving its
            // status healthy would be the same silent stop ResumeAfterFailedSwapAsync prevents on
            // the swap path. Recording it announces the freeze before the caller sees the throw.
            RecordWatchFailure(driveLetter, suspendFailure);
            throw;
        }

        try
        {
            await SwapDriveBlockAsync(drive, driveLetter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception swapFailure)
        {
            // A failed swap must not leave this drive disarmed at the source while its status still
            // reads healthy: that is the silent stop the per-drive contract forbids. Nothing was
            // swapped, so the block's header cursor is still true and the drive resumes from it.
            await ResumeAfterFailedSwapAsync(driveLetter, suspended, swapFailure, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            await ResumeDriveAfterRescanAsync(driveLetter, suspended, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception resumeFailure)
        {
            // The swap succeeded, so the drive's block is current and only its watch is missing.
            // The resume clears the drive's failure message before it arms, so without this the
            // drive would read healthy while nothing was watching it.
            RecordWatchFailure(driveLetter, resumeFailure);
            throw;
        }
    }

    /// <summary>
    ///     Puts the drive back on the watch after a swap that failed, so the caller's exception is
    ///     the only consequence. When the re-arm itself fails the drive cannot be restored, so the
    ///     freeze is made visible instead of left silent: the original failure names it on the
    ///     drive and on the fault, because that is what stopped the rescan, and the re-arm failure
    ///     travels alongside it as the reason the drive could not be put back.
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
                $"Drive {driveLetter} could not be re-armed after its rescan failed, so its watch is stopped.",
                swapFailure, resumeFailure);
        }
    }

    async Task SwapDriveBlockAsync(IndexedDrive drive, char driveLetter, CancellationToken cancellationToken)
    {
        await _swapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The check above the gate is only an early out. DisposeAsync sets the flag before it
            // waits on this same gate, so a rescan admitted after that would otherwise scan a
            // drive and publish a snapshot over an index whose blocks are already released.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryGetDriveOrdinal(driveLetter, out var driveOrdinal))
            {
                throw new ArgumentException($"Drive {driveLetter} has no block.", nameof(driveLetter));
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

            // The watch failure entry is deliberately left alone here: clearing it happens once
            // the gate is released, immediately before the re-arm, so the drive is never live
            // again while it still looks dropped.
            lock (_stateLock)
            {
                _driveBlocks[driveOrdinal] = completedScan.DriveBlock;
                _discardedBlocksByOrdinal.Remove(driveOrdinal);
                _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = completedScan.AccessDeniedSubtreeCount;
            }

            PublishSnapshot();
        }
        finally
        {
            _swapGate.Release();
        }
    }

    /// <summary>
    ///     Runs the producer for a rescan and restores the renamed-aside cache file whenever the
    ///     scan fails, is cancelled, or reports this drive failed. Null means nothing to swap.
    /// </summary>
    async Task<ScanDriveResult?> ProduceRescannedBlockAsync(IndexedDrive drive, ushort driveOrdinal,
        string blockPath, string? retiredPath, DriveBlock superseded, CancellationToken cancellationToken)
    {
        ScanDriveResult? scanResult;
        try
        {
            scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, blockPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (retiredPath is not null)
            {
                RestoreRetiredFile(retiredPath, blockPath, superseded);
            }

            throw;
        }

        if (scanResult is null && retiredPath is not null)
        {
            RestoreRetiredFile(retiredPath, blockPath, superseded);
        }

        return scanResult;
    }

    /// <summary>
    ///     Stops the rescanned drive at the watch source before the gate is taken. With no watch
    ///     running this reports nothing and the rescan behaves exactly as it did. With a session
    ///     whose pump has already finished, meaning every drive had faulted, it reclaims that
    ///     session and reports that a fresh one must be started over every drive. A rescan that
    ///     reads that pump as still running a moment before the final drive's fault ends it throws
    ///     <see cref="InvalidOperationException" /> out of the source instead, which is loud,
    ///     bounded, and clears on a retry.
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
                // This rescan is the recovery from exactly the fault that stop rethrows, so
                // rethrowing it here would fail the recovery with the failure it recovers from.
                // It was already announced through WatchFaulted when the pump observed it.
            }

            return new SuspendedWatch(null, RestartWholeSession: true, sessionToken);
        }

        await session.Source.DisarmDriveAsync(driveLetter, cancellationToken).ConfigureAwait(false);
        return new SuspendedWatch(session, RestartWholeSession: false, session.CallerToken);
    }

    /// <summary>
    ///     Puts the rescanned drive back on the watch it was taken off. A whole-session restart
    ///     links to the token the original session was linked to, not this rescan's, so a rescan
    ///     never silently reparents the watch's lifetime; the rescan's own token still cancels the
    ///     start call itself.
    /// </summary>
    async Task ResumeDriveAfterRescanAsync(char driveLetter, SuspendedWatch suspended,
        CancellationToken cancellationToken)
    {
        if (suspended.Session is { } session)
        {
            // Clear before arming, never after: a fresh batch arriving while this drive still
            // looked dropped would be discarded and lost for good, and no batch for it can exist
            // in between, because it is disarmed at the source.
            var target = BuildWatchTarget(driveLetter);
            ClearWatchFailure(driveLetter);
            await session.Source.ArmDriveAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A message about a watch failure on a block that has just been replaced is no longer
        // true, whether or not there is a session to arm this drive back onto.
        ClearWatchFailure(driveLetter);
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
    ///     Renames the file currently at <paramref name="canonicalPath" /> aside, if one exists
    ///     (a prior rescan attempt that failed after renaming but before completing may have left
    ///     nothing there, in which case there is nothing to move and null is returned), and
    ///     schedules it for deletion once <paramref name="superseded" /> is fully released. The
    ///     retired name uses a random suffix rather than a timestamp: two rescans of the same
    ///     drive within one clock tick (Windows' clock granularity is roughly 15.6 milliseconds,
    ///     and a small tree can scan faster than that) would otherwise collide while the first
    ///     retired file is still held, and <see cref="File.Move(string, string)" /> throws on a
    ///     destination that already exists.
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
    ///     Undoes <see cref="RenameAsideForRescan" /> when the scan that was meant to replace the
    ///     canonical file fails or is cancelled, so a failed rescan does not destroy an otherwise
    ///     valid warm-start cache: without this, <paramref name="superseded" />'s scheduled
    ///     delete would remove the only remaining copy of the drive's last good scan once this
    ///     process exits, forcing a needless cold scan next time even though nothing was actually
    ///     wrong with the data that was there before the rescan was attempted.
    /// </summary>
    static void RestoreRetiredFile(string retiredPath, string canonicalPath, DriveBlock superseded)
    {
        try
        {
            if (File.Exists(retiredPath))
            {
                // A failed producer can leave an incomplete file at the canonical path after
                // releasing its mapping. Remove it before restoring the last good scan.
                if (File.Exists(canonicalPath))
                {
                    File.Delete(canonicalPath);
                }

                File.Move(retiredPath, canonicalPath);
            }
        }
        catch (IOException)
        {
            // Best effort: if the move back fails, the retired file stays on disk under its
            // renamed name. CleanupRetiredSiblings removes it on the next open for this drive,
            // which then simply cold-scans instead of warm-starting - slower, never wrong.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }

        // Whether or not the move back succeeded, the schedule set for the renamed path is no
        // longer correct: either the file is back at its original name (nothing to delete under
        // the old override) or it is still at the renamed name and the next open's cleanup owns
        // it, not this block's eventual release.
        superseded.ClearScheduledDelete();
    }

    /// <summary>
    ///     Swaps in a new snapshot over the current <see cref="_driveBlocks" /> list and tracks
    ///     the retired one weakly only to avoid extending its lifetime, so it is never
    ///     force-released while a handle may retain it.
    /// </summary>
    void PublishSnapshot()
    {
        Snapshot previous;
        lock (_stateLock)
        {
            previous = _snapshot ?? throw new ObjectDisposedException(nameof(FileIndex));
            _snapshot = Snapshot.Create(_driveBlocks);
        }

        _retiredSnapshots.RemoveAll(weak => !weak.TryGetTarget(out _));
        _retiredSnapshots.Add(new WeakReference<Snapshot>(previous));
    }

    /// <summary>
    ///     Forces every retired snapshot without exposed handles to release its blocks now, so a normal
    ///     exit through <see cref="DisposeAsync" /> cleans up unheld temp and cache files immediately.
    /// </summary>
    void ReleaseAllRetiredSnapshots()
    {
        foreach (var weak in _retiredSnapshots)
        {
            if (weak.TryGetTarget(out var retired) && !retired.HasExposedHandles)
            {
                retired.ReleaseNow();
            }
        }

        _retiredSnapshots.Clear();
    }

    /// <summary>What a rescan took off the watch, and what it therefore has to put back.</summary>
    readonly record struct SuspendedWatch(WatchSession? Session, bool RestartWholeSession,
        CancellationToken SessionToken);
}
