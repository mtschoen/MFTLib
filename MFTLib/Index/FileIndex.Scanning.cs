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
            var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, ComputeScanBlockPath(drive),
                cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     <paramref name="JournalId" /> and <paramref name="NextUsn" /> are the journal cursor
    ///     armed before the block was built, already durable in the adopted block's own header
    ///     (an MFT producer stamps them before its own completion flush; an enumeration block has
    ///     no journal cursor, so these stay zero, matching the header's initialized default). A
    ///     later watch starts from this cursor.
    /// </summary>
    readonly record struct ScanDriveResult(
        DriveBlock DriveBlock,
        int AccessDeniedSubtreeCount,
        ulong JournalId = 0,
        long NextUsn = 0);

    readonly record struct BlockScanResult(BlockFile Block, EnumerationResult Result);

    /// <summary>
    ///     Picks the producer for one drive's cold scan according to
    ///     <see cref="FileIndexOptions.ProducerPolicy" />. <see cref="ProducerPolicy.MftOnly" />
    ///     requires <see cref="FileIndexOptions.MftProducer" /> and lets a producer failure
    ///     propagate, because the caller asked for exactly one producer.
    ///     <see cref="ProducerPolicy.Auto" /> prefers the MFT producer when one is set, but a
    ///     failure there is not fatal: it is recorded on the drive status and the drive falls
    ///     back to the enumeration producer instead, so no drive is ever left unindexed because
    ///     of its substrate. <see cref="ProducerPolicy.EnumerationOnly" /> ignores
    ///     <see cref="FileIndexOptions.MftProducer" /> entirely. Cancellation is never treated as
    ///     a producer failure: it always propagates rather than triggering a fallback.
    /// </summary>
    async Task<ScanDriveResult> ProduceDriveBlockAsync(IndexedDrive drive, ushort driveOrdinal, string blockPath,
        CancellationToken cancellationToken)
    {
        if (_options.ProducerPolicy == ProducerPolicy.MftOnly)
        {
            var producer = _options.MftProducer ?? throw new InvalidOperationException(
                $"{nameof(ProducerPolicy)}.{nameof(ProducerPolicy.MftOnly)} requires " +
                $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.MftProducer)} to be set.");
            return await RunMftProducerAsync(drive, driveOrdinal, blockPath, producer, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_options.ProducerPolicy == ProducerPolicy.Auto && _options.MftProducer is { } mftProducer)
        {
            try
            {
                var mftScanResult = await RunMftProducerAsync(drive, driveOrdinal, blockPath, mftProducer,
                    cancellationToken).ConfigureAwait(false);

                // A genuine MFT-producer success replaces whatever this ordinal's dictionary
                // entry recorded from an earlier failed attempt (AddDriveAsync's first scan or a
                // prior RescanAsync): DriveStatus.MftProducerFailureMessage's doc comment promises
                // null once the current block came from the MFT producer, so a recovered drive
                // must not keep reporting a stale failure. The catch block below is the only other
                // writer of this ordinal's entry, and it runs on a different path than this one, so
                // clearing here never erases a message that same call just set.
                lock (_stateLock)
                {
                    _mftProducerFailureMessagesByOrdinal.Remove(driveOrdinal);
                }

                return mftScanResult;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_stateLock)
                {
                    _mftProducerFailureMessagesByOrdinal[driveOrdinal] = exception.Message;
                }
            }
        }

        return await Task
            .Run(() => ScanDrive(drive, driveOrdinal, blockPath, _options.NoCache, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs the MFT producer for one drive and adopts its finished block exactly as a warm
    ///     start or an enumeration scan would: ownership passes to a new reference-counted
    ///     <see cref="DriveBlock" />. <paramref name="producer" />'s own
    ///     <see cref="MftBlockProduceResult.SkippedRecordCount" /> is surfaced as this drive's
    ///     access-denied subtree count, the same warning slot an enumeration walk uses for
    ///     records it could not place. <see cref="MftBlockProduceResult.JournalId" /> and
    ///     <see cref="MftBlockProduceResult.NextUsn" /> are already durable in the returned
    ///     <see cref="MftBlockProduceResult.Block" />'s header by the time it gets here (the
    ///     producer stamps them before its own <c>Complete()</c> call, the one flush-safe place to
    ///     do it), so this method does not write them again.
    ///     <see cref="MftBlockProduceResult.CompactionNeeded" /> is intentionally not read here
    ///     either: <see cref="DescribeDrive" /> derives <see cref="DriveStatus.CompactionNeeded" />
    ///     from the header's own <see cref="BlockFlags.CompactionNeeded" /> flag, which the
    ///     producer sets on the block directly (the same way <see cref="EnumerationProducer" />
    ///     does via <see cref="BlockWriter.MarkCompactionNeeded" />), so this result field would be
    ///     a redundant second copy of that same flag rather than a value this method needs to act
    ///     on. The cursor is instead used for a consistency check once <see cref="ScanDriveResult" />
    ///     is built: a producer that reports one cursor but stamped a different one into the block
    ///     it built violated its own contract, and that must not go unnoticed any more than an
    ///     on-disk block that fails validation would. It is treated as a producer failure, not
    ///     adopted as a warning on an otherwise-trusted block.
    /// </summary>
    async Task<ScanDriveResult> RunMftProducerAsync(IndexedDrive drive, ushort driveOrdinal, string blockPath,
        MftBlockProducer producer, CancellationToken cancellationToken)
    {
        var request = new MftBlockProduceRequest
        {
            DriveLetter = drive.DriveLetter,
            VolumeSerial = drive.VolumeSerial,
            BlockPath = blockPath,
            DeleteOnClose = _options.NoCache,
            Progress = _options.Progress
        };

        var produceResult = await producer(request, cancellationToken).ConfigureAwait(false);

        // produceResult.Block's ownership passes directly to the DriveBlock built here, which
        // releases it through the reference-counted Release() (see DriveBlock's own summary),
        // not through IDisposable.
        var driveBlock = new DriveBlock(drive.DriveLetter, driveOrdinal, produceResult.Block,
            deleteFileOnRelease: _options.NoCache, rootDirectoryPath: drive.RootDirectory);
        var scanResult = new ScanDriveResult(driveBlock, produceResult.SkippedRecordCount, produceResult.JournalId,
            produceResult.NextUsn);

        var header = driveBlock.Block.Header;
        if (header.UsnJournalId != scanResult.JournalId || header.UsnNextUsn != scanResult.NextUsn)
        {
            // The block was already adopted into driveBlock above, but never handed to
            // _driveBlocks (reference count is still zero), so it is released the same way
            // ReleaseUnpublishedBlocks unwinds an unpublished block: disposing Block directly,
            // not through the reference-counted Release().
            driveBlock.Block.Dispose();

            // The producer already Complete()d this block before this check ran, so it would
            // pass BlockHeader.Validate like any other valid block. Deleting it here, mirroring
            // BuildAndInitialize and RestoreRetiredFile, keeps a later TryOpenExistingBlock from
            // warm-starting off a block whose cursor invariant was just rejected.
            BlockFile.TryDeleteFailedCreate(blockPath);
            throw new InvalidOperationException(
                $"The MFT producer's block header carries journal cursor ({header.UsnJournalId}, " +
                $"{header.UsnNextUsn}) but its result reported cursor ({scanResult.JournalId}, " +
                $"{scanResult.NextUsn}). A producer that contradicts its own block cannot be trusted.");
        }

        return scanResult;
    }

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

        BlockFile? block = BlockFile.Create(new BlockFileCreateOptions
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
            var completed = new BlockScanResult(block, result);
            block = null;
            return completed;
        }
        finally
        {
            block?.Dispose();
        }
    }

}
