namespace MFTLib.Index;

public sealed partial class FileIndex
{
    enum CanonicalSlotOutcome
    {
        NotOwned,
        Owned,
        DeclinedInUse
    }

    CanonicalSlotOutcome ResolveCanonicalOwnership(IndexedDrive drive, char driveLetter, ushort driveOrdinal)
    {
        if (_options.NoCache)
        {
            return CanonicalSlotOutcome.NotOwned;
        }

        if (EnsureCanonicalOwnership(drive))
        {
            // Cache-mode rescans can leave a renamed ".retired-*" sibling if a prior attempt
            // aborted before removing it; swept here, under the held owner lock.
            CleanupRetiredSiblings(drive.DriveLetter, drive.VolumeSerial);
            return CanonicalSlotOutcome.Owned;
        }

        if (_options.InitialOpenCacheOnly)
        {
            lock (_stateLock)
            {
                _mftProducerFailureMessagesByOrdinal[driveOrdinal] =
                    $"Drive {driveLetter}: cache block is in use by another FileIndex and --cache-only forbids a scan.";
            }

            RecordFailedDrive(driveLetter, driveOrdinal, DriveFailureKind.InUse);
            return CanonicalSlotOutcome.DeclinedInUse;
        }

        return CanonicalSlotOutcome.NotOwned;
    }

    void RecordOfflineDrive(char driveLetter)
    {
        lock (_stateLock)
        {
            _blocklessDriveStatuses.Add(new DriveStatus
            {
                DriveLetter = driveLetter,
                ProducerKind = ProducerKind.Enumeration,
                BlockSource = BlockSource.None,
                State = DriveState.Offline,
                RowCount = 0,
                LiveRowCount = 0,
                ScanTimestamp = DateTime.MinValue,
                CompactionNeeded = false,
                WatchSupported = false
            });
        }
    }

    void RecordFailedDrive(char driveLetter, ushort driveOrdinal, DriveFailureKind failureKind)
    {
        lock (_stateLock)
        {
            _blocklessDriveStatuses.Add(new DriveStatus
            {
                DriveLetter = driveLetter,
                ProducerKind = ProducerKind.Mft,
                BlockSource = BlockSource.None,
                State = DriveState.Failed,
                RowCount = 0,
                LiveRowCount = 0,
                ScanTimestamp = DateTime.MinValue,
                CompactionNeeded = false,
                WatchSupported = false,
                DiscardedBlock = _discardedBlocksByOrdinal.TryGetValue(driveOrdinal, out var discarded)
                    ? discarded
                    : null,
                MftProducerFailureMessage = _mftProducerFailureMessagesByOrdinal.GetValueOrDefault(driveOrdinal),
                FailureKind = failureKind
            });
            _mftProducerFailureMessagesByOrdinal.Remove(driveOrdinal);
            _discardedBlocksByOrdinal.Remove(driveOrdinal);
            _blockSourcesByOrdinal.Remove(driveOrdinal);
            ReleaseCanonicalOwnershipLocked(driveLetter);
        }
    }

    string CanonicalBlockPath(IndexedDrive drive) =>
        Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial));

    /// <summary>
    ///     Where one drive's scan writes. <see cref="ScanBlockTarget.OwnsCanonicalSlot" /> is
    ///     the license every rename or delete of the canonical file checks: without it the
    ///     canonical file belongs to another live index and is never validated, renamed, or
    ///     deleted, and the scan goes to a private delete-on-close temp file instead.
    /// </summary>
    readonly record struct ScanBlockTarget(string Path, bool DeleteOnClose, bool OwnsCanonicalSlot);

    ScanBlockTarget ComputeScanTarget(IndexedDrive drive, bool ownsCanonicalSlot)
    {
        if (_options.NoCache)
        {
            return new ScanBlockTarget(Path.Combine(Path.GetTempPath(),
                $"mftlib-nocache-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial)}"),
                DeleteOnClose: true, OwnsCanonicalSlot: false);
        }

        if (ownsCanonicalSlot)
        {
            return new ScanBlockTarget(CanonicalBlockPath(drive), DeleteOnClose: false,
                OwnsCanonicalSlot: true);
        }

        return new ScanBlockTarget(Path.Combine(Path.GetTempPath(),
            $"mftlib-private-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial)}"),
            DeleteOnClose: true, OwnsCanonicalSlot: false);
    }

    /// <summary>
    ///     Takes the drive's per-block owner lock if this index does not already hold it.
    ///     False means another live index owns the cache slot.
    /// </summary>
    bool EnsureCanonicalOwnership(IndexedDrive drive)
    {
        var driveLetter = char.ToUpperInvariant(drive.DriveLetter);
        lock (_stateLock)
        {
            if (_canonicalLocksByLetter.ContainsKey(driveLetter))
            {
                return true;
            }
        }

        var ownerLock = BlockOwnerLock.TryAcquire(CanonicalBlockPath(drive));
        if (ownerLock is null)
        {
            return false;
        }

        lock (_stateLock)
        {
            _canonicalLocksByLetter[driveLetter] = ownerLock;
        }

        return true;
    }

    void ReleaseCanonicalOwnership(char driveLetter)
    {
        lock (_stateLock)
        {
            ReleaseCanonicalOwnershipLocked(driveLetter);
        }
    }

    /// <summary>The caller holds <see cref="_stateLock" />.</summary>
    void ReleaseCanonicalOwnershipLocked(char driveLetter)
    {
        if (_canonicalLocksByLetter.Remove(char.ToUpperInvariant(driveLetter), out var ownerLock))
        {
            ownerLock.Dispose();
        }
    }

    void CleanupRetiredSiblings(char driveLetter, uint volumeSerial)
    {
        var pattern = CacheDirectory.BlockFileName(driveLetter, volumeSerial) + ".retired-*";
        try
        {
            foreach (var path in Directory.EnumerateFiles(CacheDirectoryPath, pattern))
            {
                TryDeleteBestEffort(path,
                    "sweeping a stale \".retired-*\" sibling left by a killed process");
            }
        }
        catch (IOException)
        {
            // Best effort: an inaccessible cache directory is reported by the warm-start
            // attempt that follows, not here.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }

    void TryDeleteBestEffort(string path, string reason)
    {
        try
        {
            File.Delete(path);
            _options.Diagnostics?.Invoke($"Deleted block file '{path}': {reason}.");
        }
        catch (IOException)
        {
            // Whatever still needs this file surfaces its own error; a leftover here is either
            // rejected again next time or, for a leftover still in use, simply left alone.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }

    /// <summary>
    ///     Renames the file currently at <paramref name="canonicalPath" /> aside, if one exists,
    ///     and schedules it for deletion once <paramref name="superseded" /> is fully released.
    /// </summary>
    static string? RenameAsideForRescan(string canonicalPath, DriveBlock superseded, Action<string>? diagnostics)
    {
        if (!File.Exists(canonicalPath))
        {
            return null;
        }

        var retiredPath = $"{canonicalPath}.retired-{Guid.NewGuid():N}";
        File.Move(canonicalPath, retiredPath);
        superseded.ScheduleDeleteAt(retiredPath, diagnostics);
        return retiredPath;
    }

    /// <summary>
    ///     Undoes <see cref="RenameAsideForRescan" /> when the scan fails or is cancelled.
    /// </summary>
    static void RestoreRetiredFile(string retiredPath, string canonicalPath, DriveBlock superseded, Action<string>? diagnostics)
    {
        try
        {
            if (File.Exists(retiredPath))
            {
                if (File.Exists(canonicalPath))
                {
                    File.Delete(canonicalPath);
                    diagnostics?.Invoke(
                        $"Deleted block file '{canonicalPath}': removing the partial replacement a failed or cancelled scan left before restoring the renamed-aside cache file.");
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
}
