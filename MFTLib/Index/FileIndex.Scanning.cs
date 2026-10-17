namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Settles every configured drive and returns once each has settled: one task per drive,
    ///     each reporting <see cref="FileIndexOptions.DriveOpenProgress" /> from its own thread when it
    ///     settles. The tasks are awaited all together, so a failure or a cancellation is thrown
    ///     only after every other drive's settle has finished, which is what lets the caller
    ///     release every block that was adopted. The configured drives are recorded before the
    ///     first task starts, so no task writes them.
    /// </summary>
    async Task SettleDrivesAsync(CancellationToken cancellationToken)
    {
        foreach (var drive in _options.Drives)
        {
            _driveConfigurations.Add(drive.Drive, drive);
        }

        var total = _options.Drives.Count;
        var settling = new List<Task>(total);
        foreach (var drive in _options.Drives)
        {
            settling.Add(Task.Run(() => SettleDriveAsync(drive, total, cancellationToken), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(settling).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled open reports its cancellation, whatever else a drive's settle failed with
            // while the token was being observed.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    ///     Settles one drive, then reports its settled state to
    ///     <see cref="FileIndexOptions.DriveOpenProgress" /> synchronously on the calling thread with no
    ///     lock held. The drive's <see cref="IndexDriveOpened.SettledDriveCount" /> was claimed under
    ///     <see cref="_stateLock" /> when its final state was recorded, so a callback that is slow
    ///     or blocked holds up no other drive, and reports may overlap and arrive out of count order.
    /// </summary>
    async Task SettleDriveAsync(IndexedDrive drive, int total, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await AddDriveAsync(drive, cancellationToken).ConfigureAwait(false);
        if (_options.DriveOpenProgress is not { } openProgress)
        {
            return;
        }

        var driveKey = drive.Drive;
        int settledCount;
        lock (_stateLock)
        {
            settledCount = _settledCountsByDrive[driveKey];
        }

        openProgress.Report(new IndexDriveOpened(driveKey, settledCount, total));
    }

    /// <summary>
    ///     Settles one drive while opening: warm-starts it from its cached block or scans it into a
    ///     <see cref="PendingDriveResult" /> that nothing shared has seen yet, then adopts that result
    ///     under <see cref="_stateLock" />, which is where the drive's ordinal is assigned. A drive
    ///     that ends with no block is recorded by letter as blockless instead.
    /// </summary>
    async Task AddDriveAsync(IndexedDrive drive, CancellationToken cancellationToken)
    {
        var driveKey = drive.Drive;
        if (DriveKind.For(drive.Producer, _options.MftSource).RequiresExistingRoot &&
            !Directory.Exists(drive.RootDirectory))
        {
            RecordOfflineDrive(driveKey);
            return;
        }

        var slotOutcome = ResolveCanonicalOwnership(drive);
        if (slotOutcome == CanonicalSlotOutcome.DeclinedInUse)
        {
            RecordFailedDrive(driveKey, DriveFailureKind.InUse, new PendingDriveResult
            {
                ProducerFailureMessage =
                    $"Drive {driveKey}: cache block is in use by another FileIndex and --cache-only forbids a scan."
            });
            return;
        }

        var ownsCanonicalSlot = slotOutcome == CanonicalSlotOutcome.Owned;
        var warmStart = ownsCanonicalSlot
            ? TryOpenExistingBlock(drive)
            : new WarmStartResult(null, null);
        warmStart = RejectUnresumableCheckpoint(driveKey, warmStart);
        var opened = new PendingDriveResult
        {
            CheckpointLoss = warmStart.CheckpointLoss,
            CacheSlot = DescribeCacheSlot(ownsCanonicalSlot)
        };

        if (warmStart.Block is { } warmStartedBlock)
        {
            AdoptOpenedDrive(drive, warmStartedBlock, opened with
            {
                Block = warmStartedBlock,
                BlockSource = BlockSource.WarmStartedFromCache,
                CacheOnlyUnresumable = warmStart.CacheOnlyUnresumable
            });
            return;
        }

        if (_options.CacheMode == CacheMode.ExistingOnly)
        {
            var failureKind = warmStart.DiscardedBlock == BlockValidationResult.WrongCacheTag
                ? DriveFailureKind.CacheTagMismatch
                : DriveFailureKind.CacheDeclined;
            RecordFailedDrive(driveKey, failureKind, opened with
            {
                ProducerFailureMessage =
                    $"Drive {driveKey}: no usable cache (missing, corrupt, or incompatible) and --cache-only forbids a scan."
            });
            return;
        }

        await ScanOpenedDriveAsync(drive, ownsCanonicalSlot, opened, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adopts an opened drive's settled result: assigns its ordinal (the one of
    ///     <paramref name="replacing" />, an unpublished block an earlier attempt of the same settle
    ///     adopted, when there is one), builds its <see cref="DriveBlock" /> over
    ///     <paramref name="block" />, and records every field of the result against that ordinal in
    ///     one step under <see cref="_stateLock" />; the adoption that ends the drive's settle, one
    ///     that held its catch-up or reached the limit, also claims the drive's settle count. The
    ///     snapshot is published once every drive has settled. Returns the adopted block and the drive's count of lost catch-ups in a row.
    /// </summary>
    (DriveBlock Adopted, int ConsecutiveLostCatchUps) AdoptOpenedDrive(IndexedDrive drive, BlockFile block,
        PendingDriveResult settled, DriveBlock? replacing = null)
    {
        var runtime = GetDriveRuntime(drive.Drive);
        lock (_stateLock)
        {
            var driveOrdinal = replacing is null ? (ushort)_driveBlocks.Count : replacing.DriveOrdinal;
            var adopted = BuildDriveBlock(drive, driveOrdinal, block);
            if (replacing is null)
            {
                _driveBlocks.Add(adopted);
            }
            else
            {
                _driveBlocks[driveOrdinal] = adopted;
            }

            RecordPendingResultLocked(runtime, driveOrdinal, settled, clearsCheckpointLoss: false);
            if (settled.CatchUpLoss is null || runtime.ConsecutiveLostCatchUps >= LostCatchUpRecoveryLimit)
            {
                ClaimSettledCountLocked(drive.Drive);
            }

            return (adopted, runtime.ConsecutiveLostCatchUps);
        }
    }

    /// <summary>
    ///     What opening a drive's cached block found: the block to adopt, if any; why an existing
    ///     block was discarded; the journal checkpoint loss that rejected the block, or that a
    ///     cache-only open adopted it despite, which <see cref="CacheOnlyUnresumable" /> then says.
    /// </summary>
    readonly record struct WarmStartResult(
        BlockFile? Block,
        BlockValidationResult? DiscardedBlock,
        JournalCheckpointLoss? CheckpointLoss = null,
        bool CacheOnlyUnresumable = false);

    readonly record struct BlockScanResult(BlockFile Block, EnumerationResult Result);

    /// <summary>
    ///     Picks the producer for one drive's scan and returns what it produced, keyed by nothing:
    ///     the finished block with its access-denied count and its lost catch-up, or no block and
    ///     the selected producer's failure. Enumeration walks the directory tree; operational
    ///     failures from either policy are returned so only that drive fails. Cancellation propagates.
    ///     The caller builds the <see cref="DriveBlock" /> when it publishes.
    /// </summary>
    async Task<PendingDriveResult> ProduceDriveBlockCoreAsync(IndexedDrive drive, string blockPath,
        bool deleteOnClose, CancellationToken cancellationToken)
    {
        try
        {
            if (drive.Producer == ProducerKind.Enumeration)
            {
                using var walkLease = await EnumerationWalkLimit.EnterAsync(cancellationToken).ConfigureAwait(false);
                var (scannedBlock, walked) = await Task
                    .Run(() => CreateAndPopulateBlock(drive, blockPath, deleteOnClose, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new PendingDriveResult
                {
                    Block = scannedBlock,
                    BlockSource = BlockSource.ProducedByScan,
                    AccessDeniedSubtreeCount = walked.AccessDeniedSubtreeCount
                };
            }

            var producer = _options.MftSource?.Producer ?? throw new InvalidOperationException(
                $"{nameof(ProducerKind)}.{nameof(ProducerKind.Mft)} requires " +
                $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.MftSource)} to be set.");
            var produced = await RunMftProducerAsync(drive, blockPath, deleteOnClose, producer, cancellationToken)
                .ConfigureAwait(false);
            return new PendingDriveResult
            {
                Block = produced.Block,
                BlockSource = BlockSource.ProducedByScan,
                SkippedRecordCount = produced.SkippedRecordCount,
                CatchUpLoss = produced.CatchUpLoss
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PendingDriveResult { ProducerFailure = exception, ProducerFailureMessage = exception.Message };
        }
    }

    /// <summary>
    ///     Runs the MFT producer for one drive and checks its finished block before anything adopts
    ///     it. The returned header contains the durable pre-scan cursor, independently validated
    ///     by the producer. <see cref="DescribeOnlineDriveBlock" /> derives
    ///     <see cref="DriveBlockStatus.IsCapacityExhausted" /> from the header's own
    ///     <see cref="BlockFlags.CapacityExhausted" /> flag, which the producer sets on the block directly.
    /// </summary>
    async Task<MftBlockProduceResult> RunMftProducerAsync(IndexedDrive drive, string blockPath,
        bool deleteOnClose, MftBlockProducer producer, CancellationToken cancellationToken)
    {
        var request = new MftBlockProduceRequest
        {
            Drive = drive.Drive,
            VolumeSerial = drive.VolumeSerial,
            BlockPath = blockPath,
            DeleteOnClose = deleteOnClose,
            Progress = _options.ScanProgress,
            CacheTag = _options.CacheTag,
            Diagnostics = _options.Diagnostics
        };

        var produceResult = await producer(request, cancellationToken).ConfigureAwait(false);
        ValidateProducedBlock(produceResult, request);
        return produceResult;
    }

    /// <summary>
    ///     Drops a warm-start candidate whose journal checkpoint its source cannot safely adopt,
    ///     returning why in the result. Rejecting it here rescans once and keeps the reason,
    ///     rather than publishing stale rows or arming a watch from a lost position.
    ///     A watch-capable source checks retention and catches
    ///     up later; a scan-only source requires the journal's next USN to equal its stamped cursor.
    ///     Dump sources are never cached and never reach warm start. An enumeration block is also not
    ///     watched through the journal, so nothing about it can have fallen out of one.
    ///     <para>
    ///         <see cref="CacheMode.ExistingOnly" /> forbids the scan that would
    ///         otherwise follow a rejection, so the reasoning above does not apply to it: a
    ///         cache-only open never watches, and the block is still a correct snapshot as of
    ///         its age, so it is adopted anyway rather than failing the drive. The result says so,
    ///         and the adoption marks the block unresumable in
    ///         <see cref="_unresumableCheckpointsByOrdinal" /> so a later
    ///         <see cref="StartWatchingAsync" /> refuses to start a watch from a cursor the
    ///         journal no longer holds.
    ///     </para>
    /// </summary>
    WarmStartResult RejectUnresumableCheckpoint(DriveKey drive, WarmStartResult warmStart)
    {
        if (warmStart.Block is not { } candidate || candidate.Header.ProducerKind != ProducerKind.Mft)
        {
            return warmStart;
        }

        var accepted = false;
        try
        {
            ref readonly var header = ref candidate.Header;
            var checkpointLoss = WatchSourceOrNull is null
                ? JournalCheckpointCheck.CheckUnmoved(drive, header.UsnJournalIdentifier, header.UsnNextUsn,
                    JournalCheckpointLossDetection.DriveOpening)
                : JournalCheckpointCheck.Check(drive, header.UsnJournalIdentifier, header.UsnNextUsn,
                    JournalCheckpointLossDetection.DriveOpening);
            if (checkpointLoss is not { } loss)
            {
                accepted = true;
                return warmStart;
            }

            if (_options.CacheMode == CacheMode.ExistingOnly)
            {
                accepted = true;
                return warmStart with { CheckpointLoss = loss, CacheOnlyUnresumable = true };
            }

            return new WarmStartResult(null, warmStart.DiscardedBlock, loss);
        }
        finally
        {
            if (!accepted)
            {
                // An unadopted candidate owns no references; close its mapping directly.
                candidate.Dispose();
            }
        }
    }

    WarmStartResult TryOpenExistingBlock(IndexedDrive drive)
    {
        var path = CanonicalBlockPath(drive);
        var existedBeforeOpen = File.Exists(path);

        // Ownership of a successfully opened block passes to the DriveBlock its adoption builds,
        // which releases it through the reference-counted Release() (see DriveBlock's own
        // summary), not through IDisposable.
        if (BlockFile.Open(path, drive.VolumeSerial, out var validation) is { } block)
        {
            if (block.Header.ProducerKind != drive.Producer)
            {
                var storedProducerKind = block.Header.ProducerKind;
                block.Dispose();
                TryDeleteBestEffort(path,
                    $"cache producer kind mismatch: stored {storedProducerKind}; requested {drive.Producer}");
                return new WarmStartResult(null, null);
            }

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

            return new WarmStartResult(block, null);
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
    ///     The slot and name-pool capacities for a scan: the estimated plan, grown to twice the
    ///     drive's current block's capacities, each independently, when that block ran out. A
    ///     rescan of a tree larger than the estimate therefore plans a larger block instead of
    ///     repeating the exhausted one.
    /// </summary>
    (uint SlotCapacity, uint NamePoolCapacity) PlanCapacities(DriveKey drive, uint estimatedRows)
    {
        var slotCapacity = BlockLayout.ComputeSlotCapacity(estimatedRows);
        var namePoolCapacity = BlockLayout.ComputeNamePoolCapacity(EnumerationProducer.EstimateNamePoolBytes(estimatedRows));
        lock (_stateLock)
        {
            if (TryGetDriveOrdinalLocked(drive, out var driveOrdinal) &&
                _driveBlocks[driveOrdinal].Block.Header is { IsCapacityExhausted: true } exhausted)
            {
                slotCapacity = Math.Max(slotCapacity, BlockLayout.DoubleCapacity(exhausted.SlotCapacity));
                namePoolCapacity = Math.Max(namePoolCapacity, BlockLayout.DoubleCapacity(exhausted.NamePoolCapacity));
            }
        }

        return (slotCapacity, namePoolCapacity);
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
        var (slotCapacity, namePoolCapacity) = PlanCapacities(drive.Drive,
            EnumerationProducer.EstimateRowCount(drive.RootDirectory));

        BlockFile? block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = blockPath,
            VolumeSerial = drive.VolumeSerial,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity,
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
                Drive = drive.Drive
            });

            var result = producer.Produce(writer, _options.ScanProgress, cancellationToken);
            writer.Complete(_options.TimeProvider.GetUtcNow().UtcDateTime, null);
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
