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
    public async Task RescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_driveConfigurations.TryGetValue(char.ToUpperInvariant(driveLetter), out var drive))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

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

            ScanDriveResult scanResult;
            try
            {
                scanResult = await Task
                    .Run(() => ScanDrive(drive, driveOrdinal, blockPath, _options.NoCache, cancellationToken),
                        cancellationToken)
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

            lock (_stateLock)
            {
                _driveBlocks[driveOrdinal] = scanResult.DriveBlock;
                _discardedBlocksByOrdinal.Remove(driveOrdinal);
                _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = scanResult.AccessDeniedSubtreeCount;
            }

            PublishSnapshot();
        }
        finally
        {
            _swapGate.Release();
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
                // ScanDrive's own failure cleanup disposes the failed attempt's partial block
                // without deleting it (a cache-mode block is never created with delete-on-close,
                // since a successful one must not self-delete), so an incomplete file can be
                // sitting at the canonical path. It is worthless - the next open would reject it
                // as Incomplete anyway - and must not block reclaiming the name for the retired
                // file, which still holds the last good scan.
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
    ///     the retired one weakly, so <see cref="DisposeAsync" /> can force its release
    ///     deterministically even if nothing else ever references it long enough for the
    ///     finalizer to run. The retired snapshot is not force-released here: a caller may still
    ///     hold a <see cref="FileEntry" /> minted from it, and <see cref="Snapshot.ReleaseNow" />
    ///     is reserved for the index's own deterministic teardown in <see cref="DisposeAsync" />.
    /// </summary>
    void PublishSnapshot()
    {
        Snapshot previous;
        lock (_stateLock)
        {
            previous = _snapshot;
            _snapshot = Snapshot.Create(_driveBlocks);
        }

        _retiredSnapshots.RemoveAll(weak => !weak.TryGetTarget(out _));
        _retiredSnapshots.Add(new WeakReference<Snapshot>(previous));
    }

    /// <summary>
    ///     Forces every retired snapshot still reachable through <see cref="_retiredSnapshots" />
    ///     to release its blocks now, so a normal exit through <see cref="DisposeAsync" /> cleans
    ///     up every superseded no-cache temp file and rescan-retired cache file even if the
    ///     process never happened to trigger a finalizer for one in between.
    /// </summary>
    void ReleaseAllRetiredSnapshots()
    {
        foreach (var weak in _retiredSnapshots)
        {
            if (weak.TryGetTarget(out var retired))
            {
                retired.ReleaseNow();
            }
        }

        _retiredSnapshots.Clear();
    }
}
