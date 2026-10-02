namespace MFTLib.Index;

public sealed partial class FileIndex
{
    enum CanonicalSlotOutcome
    {
        NotOwned,
        Owned,
        DeclinedInUse
    }

    CanonicalSlotOutcome ResolveCanonicalOwnership(IndexedDrive drive)
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

        return _options.InitialOpenCacheOnly ? CanonicalSlotOutcome.DeclinedInUse : CanonicalSlotOutcome.NotOwned;
    }

    void RecordOfflineDrive(char driveLetter)
    {
        lock (_stateLock)
        {
            ClaimSettledCountLocked(driveLetter);
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

    /// <summary>
    ///     Records a drive that opening left with no block, from its settled result and keyed by
    ///     letter, since a drive that never adds a block never takes an ordinal.
    /// </summary>
    void RecordFailedDrive(char driveLetter, DriveFailureKind failureKind, PendingDriveResult settled)
    {
        lock (_stateLock)
        {
            ClaimSettledCountLocked(driveLetter);
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
                DiscardedBlock = settled.DiscardedBlock,
                MftProducerFailureMessage = settled.ProducerFailureMessage,
                FailureKind = failureKind,
                // A cache-only open declines the drive precisely because the checkpoint was
                // lost, so this is where that reason has to reach the consumer: the drive ends
                // up with no block, and so never travels through DescribeDrive.
                CheckpointLoss = settled.CheckpointLoss
            });
            ReleaseCanonicalOwnershipLocked(driveLetter);
        }
    }

    string CanonicalBlockPath(IndexedDrive drive) =>
        Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial));

    CacheSlotState DescribeCacheSlot(bool ownsCanonicalSlot) =>
        _options.NoCache
            ? CacheSlotState.NotApplicable
            : ownsCanonicalSlot
                ? CacheSlotState.OwnedCanonical
                : CacheSlotState.PrivateFallback;

    /// <summary>
    ///     Where one drive's scan writes. <see cref="ScanBlockTarget.OwnsCanonicalSlot" /> is
    ///     the license every rename or delete of the canonical file checks: without it the
    ///     canonical file belongs to another live index and is never validated, renamed, or
    ///     deleted, and the scan goes to a private delete-on-close temp file instead.
    /// </summary>
    readonly record struct ScanBlockTarget(string Path, bool DeleteOnClose, bool OwnsCanonicalSlot,
        bool ExistedBeforeScan = false);

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
            var canonicalPath = CanonicalBlockPath(drive);
            return new ScanBlockTarget(canonicalPath, DeleteOnClose: false, OwnsCanonicalSlot: true,
                ExistedBeforeScan: File.Exists(canonicalPath));
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

    /// <summary>The caller holds <see cref="_stateLock" />.</summary>
    void ReleaseCanonicalOwnershipLocked(char driveLetter)
    {
        if (_canonicalLocksByLetter.Remove(char.ToUpperInvariant(driveLetter), out var ownerLock))
        {
            ownerLock.Dispose();
        }
    }

    /// <summary>
    ///     Lists the cache directory's files matching a pattern: <see cref="Directory.EnumerateFiles(string, string)" />
    ///     unless the options supplied <see cref="FileIndexOptions.EnumerateCacheFilesForTest" />.
    /// </summary>
    readonly Func<string, string, IEnumerable<string>> _enumerateCacheFiles;

    void CleanupRetiredSiblings(char driveLetter, uint volumeSerial)
    {
        var pattern = CacheDirectory.BlockFileName(driveLetter, volumeSerial) + ".retired-*";
        try
        {
            foreach (var path in _enumerateCacheFiles(CacheDirectoryPath, pattern))
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

    /// <summary>A canonical cache file renamed aside so a scan can write the drive's new block in its place.</summary>
    readonly record struct RetiredCanonicalFile(string RetiredPath, string CanonicalPath, DriveBlock Superseded);

    /// <summary>
    ///     Renames the file at <paramref name="target" />'s canonical path aside, when the scan
    ///     writes there, <paramref name="superseded" /> is the block that file backs, and a file
    ///     exists, and schedules it for deletion once <paramref name="superseded" /> is fully
    ///     released. Null when nothing was renamed.
    /// </summary>
    RetiredCanonicalFile? RenameAsideForRescan(ScanBlockTarget target, DriveBlock? superseded)
    {
        if (superseded is null || !target.OwnsCanonicalSlot || !File.Exists(target.Path))
        {
            return null;
        }

        var retiredPath = $"{target.Path}.retired-{Guid.NewGuid():N}";
        File.Move(target.Path, retiredPath);
        superseded.ScheduleDeleteAt(retiredPath, _options.Diagnostics);
        return new RetiredCanonicalFile(retiredPath, target.Path, superseded);
    }

    /// <summary>
    ///     Cleans up after production into <paramref name="target" /> ended without a block (failed
    ///     or cancelled). A renamed-aside file is restored, which also deletes the partial
    ///     replacement. With nothing renamed, a canonical target's file is deleted here, while
    ///     this index still holds the slot's owner lock; a private target deletes itself on close.
    ///     A file that sat in the slot before the scan (a complete block whose checkpoint the journal
    ///     no longer holds) is judged by what is there now, not by who wrote it: it stays only while
    ///     it is still a complete, valid block, and a truncated or half-written replacement goes.
    /// </summary>
    void DiscardUnproducedTarget(IndexedDrive drive, ScanBlockTarget target, RetiredCanonicalFile? retired)
    {
        if (retired is not null)
        {
            RestoreRetiredFile(retired);
        }
        else if (target.OwnsCanonicalSlot &&
                 !(target.ExistedBeforeScan && HoldsCompleteBlock(target.Path, drive.VolumeSerial)))
        {
            DeletePartialCanonicalBlock(target.Path);
        }
    }

    /// <summary>
    ///     True when the file at <paramref name="path" /> still opens as a complete block for the
    ///     volume, the same validation the open path applies before it adopts a cached block.
    /// </summary>
    static bool HoldsCompleteBlock(string path, uint volumeSerial)
    {
        // Open reports an unreadable or invalid file as null rather than throwing.
        using var block = BlockFile.Open(path, volumeSerial, out _);
        return block is not null;
    }

    /// <summary>
    ///     Deletes the partial block a failed or cancelled scan left at the canonical path and reports
    ///     the delete, or the failure to delete, through <see cref="FileIndexOptions.Diagnostics" />.
    ///     A failure here never replaces the scan's own exception: the next open rejects whatever
    ///     is left as incomplete.
    /// </summary>
    void DeletePartialCanonicalBlock(string path)
    {
        string report;
        try
        {
            var existed = File.Exists(path);
            File.Delete(path);
            if (!existed)
            {
                return;
            }

            report =
                $"Deleted block file '{path}': removing the partial block a failed or cancelled scan left in the canonical cache slot.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report =
                $"Could not delete block file '{path}': the partial block a failed or cancelled scan left in the canonical cache slot ({exception.Message}).";
        }

        try
        {
            _options.Diagnostics?.Invoke(report);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The callback is the consumer's; its failure must not replace the scan's own exception.
        }
    }

    /// <summary>
    ///     Undoes <see cref="RenameAsideForRescan" /> when the scan fails or is cancelled. Nothing to
    ///     do when nothing was renamed.
    /// </summary>
    void RestoreRetiredFile(RetiredCanonicalFile? retired)
    {
        if (retired is not { } renamed)
        {
            return;
        }

        var (retiredPath, canonicalPath, superseded) = renamed;
        var diagnostics = _options.Diagnostics;
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

    /// <summary>
    ///     Swaps in a new snapshot over the current <see cref="_driveBlocks" /> list, which the
    ///     caller has just committed. The caller holds <see cref="_stateLock" />, which is what
    ///     orders every publication and every change to <see cref="_retiredSnapshots" />.
    /// </summary>
    void PublishSnapshotLocked()
    {
        RetireCurrentSnapshotLocked(Snapshot.Create(_driveBlocks));
    }

    /// <summary>
    ///     Makes <paramref name="replacement" /> the current snapshot and keeps the previous one's
    ///     release state until its release completes. The caller holds <see cref="_stateLock" />.
    /// </summary>
    void RetireCurrentSnapshotLocked(Snapshot replacement)
    {
        var previous = _snapshot ?? throw new ObjectDisposedException(nameof(FileIndex));
        _snapshot = replacement;
        _retiredSnapshots.RemoveAll(retired => retired.IsReleaseComplete);
        _retiredSnapshots.Add(previous.ReleaseState);
    }

    void ValidateProducedCacheTag(MftBlockProduceResult result, string blockPath)
    {
        var stored = result.Block.Header.CacheTag;
        if (stored == _options.CacheTag)
        {
            return;
        }

        result.Block.Dispose();
        BlockFile.TryDeleteFailedCreate(blockPath, _options.Diagnostics,
            "the MFT producer's block failed the cache tag consistency check");
        throw new InvalidOperationException(
            $"The MFT producer's block carries cache tag {stored}, but the request requires {_options.CacheTag}.");
    }

    WarmStartResult RejectCacheTag(BlockFile block, string path)
    {
        var stored = block.Header.CacheTag;
        block.Dispose();
        _options.Diagnostics?.Invoke(
            $"Cache tag mismatch for '{path}': stored {stored}; requested {_options.CacheTag}.");
        TryDeleteBestEffort(path, $"cache validation failed: {BlockValidationResult.WrongCacheTag}");
        return new WarmStartResult(null, BlockValidationResult.WrongCacheTag);
    }
}
