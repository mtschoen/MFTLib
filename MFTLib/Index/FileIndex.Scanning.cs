namespace MFTLib.Index;

public sealed partial class FileIndex
{
    async Task AddDriveAsync(IndexedDrive drive, CancellationToken cancellationToken)
    {
        var driveLetter = char.ToUpperInvariant(drive.DriveLetter);
        _driveConfigurations[driveLetter] = drive;
        if (!Directory.Exists(drive.RootDirectory))
        {
            RecordOfflineDrive(driveLetter);
            return;
        }

        ushort driveOrdinal;
        lock (_stateLock)
        {
            driveOrdinal = (ushort)_driveBlocks.Count;
        }

        var slotOutcome = ResolveCanonicalOwnership(drive, driveLetter, driveOrdinal);
        if (slotOutcome == CanonicalSlotOutcome.DeclinedInUse)
        {
            return;
        }

        var ownsCanonicalSlot = slotOutcome == CanonicalSlotOutcome.Owned;
        var warmStart = ownsCanonicalSlot
            ? TryOpenExistingBlock(drive, driveOrdinal)
            : new WarmStartResult(null, null);
        if (warmStart.DiscardedBlock is { } discardReason)
        {
            lock (_stateLock)
            {
                _discardedBlocksByOrdinal[driveOrdinal] = discardReason;
            }
        }

        warmStart = RejectUnresumableCheckpoint(driveLetter, driveOrdinal, warmStart);

        DriveBlock driveBlock;
        if (warmStart.DriveBlock is { } warmStartedBlock)
        {
            driveBlock = warmStartedBlock;
            lock (_stateLock)
            {
                _blockSourcesByOrdinal[driveOrdinal] = BlockSource.WarmStartedFromCache;
            }
        }
        else
        {
            if (_options.InitialOpenCacheOnly)
            {
                lock (_stateLock)
                {
                    _mftProducerFailureMessagesByOrdinal[driveOrdinal] =
                        $"Drive {driveLetter}: no usable cache (missing, corrupt, or incompatible) and --cache-only forbids a scan.";
                }

                var failureKind = warmStart.DiscardedBlock == BlockValidationResult.WrongCacheTag
                    ? DriveFailureKind.CacheTagMismatch
                    : DriveFailureKind.CacheDeclined;
                RecordFailedDrive(driveLetter, driveOrdinal, failureKind);
                return;
            }

            var target = ComputeScanTarget(drive, ownsCanonicalSlot);
            var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, target.Path,
                target.DeleteOnClose, cancellationToken).ConfigureAwait(false);
            if (scanResult is not { } completedScan)
            {
                RecordFailedDrive(driveLetter, driveOrdinal, DriveFailureKind.ProducerFailed);
                return;
            }

            driveBlock = completedScan.DriveBlock;
            lock (_stateLock)
            {
                _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = completedScan.AccessDeniedSubtreeCount;
                _blockSourcesByOrdinal[driveOrdinal] = BlockSource.ProducedByScan;
            }
        }

        lock (_stateLock)
        {
            _driveBlocks.Add(driveBlock);
        }
    }

    /// <summary>
    ///     Opens one drive, then reports its settled state to <paramref name="openProgress" />.
    ///     The settled status is re-read under <see cref="_stateLock" /> through
    ///     <see cref="DescribeSettledDrive" /> without traversing previously settled drives.
    ///     If <see cref="AddDriveAsync" /> completed synchronously, progress reports
    ///     synchronously without allocating an async state machine.
    /// </summary>
    Task AddDriveWithProgressAsync(IndexedDrive drive, int openOrdinal, int openTotal,
        IProgress<IndexDriveOpened> openProgress, CancellationToken cancellationToken)
    {
        var task = AddDriveAsync(drive, cancellationToken);
        if (task.IsCompletedSuccessfully)
        {
            ReportSettledDrive(drive, openOrdinal, openTotal, openProgress);
            return Task.CompletedTask;
        }

        return ReportSettledDriveAwaitedAsync(task, drive, openOrdinal, openTotal, openProgress);
    }

    async Task ReportSettledDriveAwaitedAsync(Task task, IndexedDrive drive, int openOrdinal, int openTotal,
        IProgress<IndexDriveOpened> openProgress)
    {
        await task.ConfigureAwait(false);
        ReportSettledDrive(drive, openOrdinal, openTotal, openProgress);
    }

    void ReportSettledDrive(IndexedDrive drive, int openOrdinal, int openTotal,
        IProgress<IndexDriveOpened> openProgress)
    {
        var settled = DescribeSettledDrive(char.ToUpperInvariant(drive.DriveLetter));
        openProgress.Report(new IndexDriveOpened
        {
            DriveLetter = settled.DriveLetter,
            Ordinal = openOrdinal,
            Total = openTotal,
            BlockSource = settled.BlockSource,
            State = settled.State
        });
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
    ///     Picks the producer for one drive's cold scan. Enumeration walks the directory tree;
    ///     MFT failures return null so the caller can mark only that drive failed. Cancellation
    ///     always propagates.
    /// </summary>
    async Task<ScanDriveResult?> ProduceDriveBlockAsync(IndexedDrive drive, ushort driveOrdinal, string blockPath,
        bool deleteOnClose, CancellationToken cancellationToken)
    {
        if (_options.ProducerPolicy == ProducerPolicy.Enumeration)
        {
            return await Task
                .Run(() => ScanDrive(drive, driveOrdinal, blockPath, deleteOnClose, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var producer = _options.MftProducer ?? throw new InvalidOperationException(
            $"{nameof(ProducerPolicy)}.{nameof(ProducerPolicy.Mft)} requires " +
            $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.MftProducer)} to be set.");

        try
        {
            var mftScanResult = await RunMftProducerAsync(drive, driveOrdinal, blockPath, deleteOnClose, producer,
                cancellationToken).ConfigureAwait(false);
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

            return null;
        }
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
        bool deleteOnClose, MftBlockProducer producer, CancellationToken cancellationToken)
    {
        var request = new MftBlockProduceRequest
        {
            DriveLetter = drive.DriveLetter,
            VolumeSerial = drive.VolumeSerial,
            BlockPath = blockPath,
            DeleteOnClose = deleteOnClose,
            Progress = _options.Progress,
            CacheTag = _options.CacheTag
        };

        var produceResult = await producer(request, cancellationToken).ConfigureAwait(false);
        ValidateProducedCacheTag(produceResult, blockPath);

        // produceResult.Block's ownership passes directly to the DriveBlock built here, which
        // releases it through the reference-counted Release() (see DriveBlock's own summary),
        // not through IDisposable.
        var driveBlock = new DriveBlock(drive.DriveLetter, driveOrdinal, produceResult.Block,
            rootDirectoryPath: drive.RootDirectory);
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
            BlockFile.TryDeleteFailedCreate(blockPath, _options.Diagnostics,
                "the MFT producer's block failed the journal cursor consistency check");
            throw new InvalidOperationException(
                $"The MFT producer's block header carries journal cursor ({header.UsnJournalId}, " +
                $"{header.UsnNextUsn}) but its result reported cursor ({scanResult.JournalId}, " +
                $"{scanResult.NextUsn}). A producer that contradicts its own block cannot be trusted.");
        }

        return scanResult;
    }

    /// <summary>
    ///     Drops a warm-start candidate whose journal checkpoint the journal no longer holds,
    ///     recording why. Adopting such a block arms a watch that dies on its first read and
    ///     rescans anyway, with nothing left to tell the consumer why; rejecting it here
    ///     rescans once and keeps the reason. Only an MFT-backed block carries a checkpoint:
    ///     an enumeration block is not watched through the journal, so nothing about it can
    ///     have fallen out of one.
    ///     <para>
    ///         <see cref="FileIndexOptions.InitialOpenCacheOnly" /> forbids the scan that would
    ///         otherwise follow a rejection, so the reasoning above does not apply to it: a
    ///         cache-only open never watches, and the block is still a correct snapshot as of
    ///         its age, so it is adopted anyway rather than failing the drive. The ordinal is
    ///         recorded in <see cref="_cacheOnlyUnresumableCheckpointOrdinals" /> so a later
    ///         <see cref="StartWatchingAsync" /> does not silently arm a watch from a cursor the
    ///         journal no longer holds.
    ///     </para>
    /// </summary>
    WarmStartResult RejectUnresumableCheckpoint(char driveLetter, ushort driveOrdinal, WarmStartResult warmStart)
    {
        if (warmStart.DriveBlock is not { ProducerKind: ProducerKind.Mft } candidate)
        {
            return warmStart;
        }

        var accepted = false;
        try
        {
            ref readonly var header = ref candidate.Block.Header;
            if (JournalCheckpointCheck.Check(driveLetter, header.UsnJournalId, header.UsnNextUsn,
                    JournalCheckpointLossDetection.DriveOpening) is not { } loss)
            {
                accepted = true;
                return warmStart;
            }

            lock (_stateLock)
            {
                _checkpointLossesByOrdinal[driveOrdinal] = loss;
            }

            if (_options.InitialOpenCacheOnly)
            {
                lock (_stateLock)
                {
                    _cacheOnlyUnresumableCheckpointOrdinals.Add(driveOrdinal);
                }

                accepted = true;
                return warmStart;
            }

            return new WarmStartResult(null, warmStart.DiscardedBlock);
        }
        finally
        {
            if (!accepted)
            {
                // An unpublished candidate owns no references; close its mapping directly.
                candidate.Block.Dispose();
            }
        }
    }

    WarmStartResult TryOpenExistingBlock(IndexedDrive drive, ushort driveOrdinal)
    {
        var path = CanonicalBlockPath(drive);
        var existedBeforeOpen = File.Exists(path);

        // Ownership of a successfully opened block passes directly to the DriveBlock built in
        // the same expression below, which releases it through the reference-counted Release()
        // (see DriveBlock's own summary), not through IDisposable.
        if (BlockFile.Open(path, drive.VolumeSerial, out var validation) is { } block)
        {
            if (block.Header.CacheTag != _options.CacheTag)
            {
                return RejectCacheTag(block, path);
            }

            if (block.Header.ProducerKind == ProducerKind.Enumeration)
            {
                var cachedRoot = NamePool.ReadRowName(block, 0);
                if (!NameMatching.EqualsName(cachedRoot, drive.RootDirectory, caseSensitive: !OperatingSystem.IsWindows()))
                {
                    block.Dispose();
                    TryDeleteBestEffort(path, $"cache validation failed: {BlockValidationResult.WrongRootDirectory}");
                    return new WarmStartResult(null, BlockValidationResult.WrongRootDirectory);
                }
            }

            return new WarmStartResult(
                new DriveBlock(drive.DriveLetter, driveOrdinal, block, rootDirectoryPath: drive.RootDirectory), null);
        }

        if (validation != BlockValidationResult.WrongMagic || existedBeforeOpen)
        {
            TryDeleteBestEffort(path, $"cache validation failed: {validation}");
        }

        // A block is only "discarded" when one genuinely existed and was rejected; a first-ever
        // scan with nothing at the path is not a discard.
        return new WarmStartResult(null, existedBeforeOpen ? validation : null);
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
            new DriveBlock(drive.DriveLetter, driveOrdinal, block, rootDirectoryPath: drive.RootDirectory),
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
            DeleteOnClose = deleteOnClose,
            Diagnostics = _options.Diagnostics,
            CacheTag = _options.CacheTag
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
