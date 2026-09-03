namespace MFTLib.Index;

public sealed partial class FileIndex
{
    async Task AddDriveAsync(IndexedDrive drive, CancellationToken cancellationToken)
    {
        var driveLetter = char.ToUpperInvariant(drive.DriveLetter);
        _driveConfigurations[driveLetter] = drive;

        if (!Directory.Exists(drive.RootDirectory))
        {
            lock (_stateLock)
            {
                _offlineDrives.Add(new DriveStatus
                {
                    DriveLetter = driveLetter,
                    ProducerKind = ProducerKind.Enumeration,
                    State = DriveState.Offline,
                    RowCount = 0,
                    ScanTimestamp = DateTime.MinValue,
                    CompactionNeeded = false,
                    WatchSupported = false
                });
            }

            return;
        }

        ushort driveOrdinal;
        lock (_stateLock)
        {
            driveOrdinal = (ushort)_driveBlocks.Count;
        }

        // A process that was killed rather than disposed can leave a leftover behind: a
        // no-cache temp block (DisposeAsync is what deletes those; see FileIndexOptions.NoCache)
        // or a cache-mode ".retired-*" sibling from a rescan that renamed the old file aside but
        // never got to complete the replacement. Both are recognized purely by name and are
        // safe to remove before this drive is opened.
        if (_options.NoCache)
        {
            CleanupStaleNoCacheBlocks(drive.DriveLetter, drive.VolumeSerial);
        }
        else
        {
            CleanupRetiredSiblings(drive.DriveLetter, drive.VolumeSerial);
        }

        var warmStart = TryOpenExistingBlock(drive, driveOrdinal);
        if (warmStart.DiscardedBlock is { } discardReason)
        {
            lock (_stateLock)
            {
                _discardedBlocksByOrdinal[driveOrdinal] = discardReason;
            }
        }

        DriveBlock driveBlock;
        if (warmStart.DriveBlock is { } warmStartedBlock)
        {
            driveBlock = warmStartedBlock;
        }
        else
        {
            var scanResult = await Task
                .Run(() => ScanDrive(drive, driveOrdinal, ComputeScanBlockPath(drive), _options.NoCache,
                    cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            driveBlock = scanResult.DriveBlock;
            lock (_stateLock)
            {
                _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = scanResult.AccessDeniedSubtreeCount;
            }
        }

        lock (_stateLock)
        {
            _driveBlocks.Add(driveBlock);
        }
    }

    readonly record struct WarmStartResult(DriveBlock? DriveBlock, BlockValidationResult? DiscardedBlock);

    readonly record struct ScanDriveResult(DriveBlock DriveBlock, int AccessDeniedSubtreeCount);

    readonly record struct BlockScanResult(BlockFile Block, EnumerationResult Result);

    WarmStartResult TryOpenExistingBlock(IndexedDrive drive, ushort driveOrdinal)
    {
        if (_options.NoCache)
        {
            return new WarmStartResult(null, null);
        }

        var path = Path.Combine(CacheDirectoryPath,
            CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial));
        var existedBeforeOpen = File.Exists(path);

        // Ownership of a successfully opened block passes directly to the DriveBlock built in
        // the same expression below, which releases it through the reference-counted Release()
        // (see DriveBlock's own summary), not through IDisposable.
        if (BlockFile.Open(path, drive.VolumeSerial, out var validation) is { } block)
        {
            if (block.Header.ProducerKind == ProducerKind.Enumeration)
            {
                var cachedRoot = NamePool.ReadRowName(block, 0);
                if (!NameMatching.EqualsName(cachedRoot, drive.RootDirectory, caseSensitive: !OperatingSystem.IsWindows()))
                {
                    block.Dispose();
                    TryDeleteBestEffort(path);
                    return new WarmStartResult(null, BlockValidationResult.WrongRootDirectory);
                }
            }

            return new WarmStartResult(
                new DriveBlock(drive.DriveLetter, driveOrdinal, block, deleteFileOnRelease: false,
                    rootDirectoryPath: drive.RootDirectory), null);
        }

        if (validation != BlockValidationResult.WrongMagic || existedBeforeOpen)
        {
            TryDeleteBestEffort(path);
        }

        // A block is only "discarded" when one genuinely existed and was rejected; a first-ever
        // scan with nothing at the path is not a discard.
        return new WarmStartResult(null, existedBeforeOpen ? validation : null);
    }

    string ComputeScanBlockPath(IndexedDrive drive)
    {
        return _options.NoCache
            ? Path.Combine(Path.GetTempPath(),
                $"mftlib-nocache-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial)}")
            : Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(drive.DriveLetter, drive.VolumeSerial));
    }

    /// <summary>
    ///     Cold-scans one drive: obtains a freshly populated block from
    ///     <see cref="CreateAndPopulateBlock" /> and hands its ownership to a new reference-counted
    ///     <see cref="DriveBlock" />, reporting that block together with the number of subtrees the
    ///     scan could not enter. Nothing is caught here, so a failed or cancelled scan propagates
    ///     with no block to release: <see cref="CreateAndPopulateBlock" /> has already disposed the
    ///     partially written one.
    /// </summary>
    ScanDriveResult ScanDrive(IndexedDrive drive, ushort driveOrdinal, string blockPath, bool deleteOnClose,
        CancellationToken cancellationToken)
    {
        var (block, result) = CreateAndPopulateBlock(drive, blockPath, deleteOnClose, cancellationToken);

        // block's ownership passes directly to the DriveBlock built here, which releases it
        // through the reference-counted Release() (see DriveBlock's own summary), not through
        // IDisposable.
        return new ScanDriveResult(
            new DriveBlock(drive.DriveLetter, driveOrdinal, block, deleteFileOnRelease: deleteOnClose,
                rootDirectoryPath: drive.RootDirectory),
            result.AccessDeniedSubtreeCount);
    }

    /// <summary>
    ///     Creates a fresh block at <paramref name="blockPath" /> and runs the enumeration
    ///     producer to completion. If the producer throws for any reason, including
    ///     cancellation, the partially written block is disposed (deleting its file when
    ///     <paramref name="deleteOnClose" /> is set) before the exception propagates, so a failed
    ///     or cancelled scan never leaves an unreachable mapping, and a no-cache attempt never
    ///     leaves an orphaned temp file, behind. The returned <see cref="BlockFile" /> is the
    ///     caller's to own from here.
    /// </summary>
    BlockScanResult CreateAndPopulateBlock(IndexedDrive drive, string blockPath, bool deleteOnClose,
        CancellationToken cancellationToken)
    {
        var estimatedRows = EnumerationProducer.EstimateRowCount(drive.RootDirectory);

        // block's fate is unambiguous on every path: returned directly on success, disposed in
        // the catch below on failure. A disposal-tracking analyzer still flags this declaration,
        // because it cannot follow a try/catch whose success path returns the value rather than
        // disposing it locally; every restructuring tried here (inlining, moving the return
        // inside the try, extracting this very helper) hits the same limit. This is a documented
        // false positive, not a leak.
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = blockPath,
            VolumeSerial = drive.VolumeSerial,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(estimatedRows),
            NamePoolCapacity =
                BlockLayout.ComputeNamePoolCapacity(EnumerationProducer.EstimateNamePoolBytes(estimatedRows)),
            DeleteOnClose = deleteOnClose
        });

        try
        {
            var writer = new BlockWriter(block);
            var producer = new EnumerationProducer(new EnumerationProducerOptions
            {
                RootDirectory = drive.RootDirectory,
                DriveLetter = drive.DriveLetter
            });

            var result = producer.Produce(writer, _options.Progress, cancellationToken);
            writer.Complete(DateTime.UtcNow);
            return new BlockScanResult(block, result);
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    void CleanupRetiredSiblings(char driveLetter, uint volumeSerial)
    {
        var pattern = CacheDirectory.BlockFileName(driveLetter, volumeSerial) + ".retired-*";
        try
        {
            foreach (var path in Directory.EnumerateFiles(CacheDirectoryPath, pattern))
            {
                TryDeleteBestEffort(path);
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

    static void CleanupStaleNoCacheBlocks(char driveLetter, uint volumeSerial)
    {
        var pattern = $"mftlib-nocache-*-{CacheDirectory.BlockFileName(driveLetter, volumeSerial)}";
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
            {
                TryDeleteBestEffort(path);
            }
        }
        catch (IOException)
        {
            // Guards Directory.EnumerateFiles itself (for example the temp directory is
            // briefly inaccessible), not the deletes it drives: a leftover another running
            // instance still has mapped is not a concern here, because BlockFile opens with
            // FileShare.Delete, so unlinking it succeeds and that instance keeps reading its
            // own mapping undisturbed. A leftover that genuinely cannot be deleted is retried
            // on the next open.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }

    static void TryDeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
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
}
