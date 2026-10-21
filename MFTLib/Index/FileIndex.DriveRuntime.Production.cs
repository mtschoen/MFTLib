namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Picks the producer for one drive's scan and returns what it produced, keyed by nothing:
        ///     the finished block with its access-denied count and its lost catch-up, or no block and
        ///     the selected producer's failure. Enumeration walks the directory tree; operational
        ///     failures from either policy are returned so only that drive fails. Cancellation propagates.
        ///     The caller builds the <see cref="DriveBlock" /> when it publishes.
        /// </summary>
        async Task<PendingDriveResult> ProduceDriveBlockCoreAsync(string blockPath,
            bool deleteOnClose, CancellationToken cancellationToken)
        {
            try
            {
                if (Configuration.Producer == ProducerKind.Enumeration)
                {
                    using var walkLease = await EnumerationWalkLimit.EnterAsync(cancellationToken).ConfigureAwait(false);
                    // ReSharper disable once AccessToDisposedClosure
                    var scannedBlock = await Task
                        .Run(() => CreateAndPopulateBlock(blockPath, deleteOnClose, walkLease, cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return new PendingDriveResult
                    {
                        Block = scannedBlock,
                        Origin = BlockOrigin.ProducedByScan
                    };
                }

                if (Options.MftSource is not { } source)
                {
                    return new PendingDriveResult
                    {
                        ScanFailureKind = DriveFailureKind.MftSourceMissing,
                        ProducerFailureMessage = $"Drive {Drive}: no MFT source is available to scan this drive."
                    };
                }

                var producer = source.Producer;
                var produced = await RunMftProducerAsync(blockPath, deleteOnClose, producer, cancellationToken)
                    .ConfigureAwait(false);
                return new PendingDriveResult
                {
                    Block = produced.Block,
                    Origin = BlockOrigin.ProducedByScan,
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
        ///     by the producer. <see cref="DescribeLocked" /> derives
        ///     <see cref="DriveBlockStatus.IsCapacityExhausted" /> from the header's own
        ///     <see cref="BlockFlags.CapacityExhausted" /> flag, which the producer sets on the block directly.
        /// </summary>
        async Task<MftBlockProduceResult> RunMftProducerAsync(string blockPath,
            bool deleteOnClose, MftBlockProducer producer, CancellationToken cancellationToken)
        {
            var request = new MftBlockProduceRequest
            {
                Drive = Drive,
                VolumeSerial = Configuration.VolumeSerial,
                BlockPath = blockPath,
                DeleteOnClose = deleteOnClose,
                Progress = Options.ScanProgress,
                CacheTag = Options.CacheTag,
                ScanShape = ExpectedScanShape,
                Diagnostics = Options.Diagnostics,
                RetainedFileNames = Options.RetainedFileNames
            };

            var produceResult = await producer(request, cancellationToken).ConfigureAwait(false);
            ValidateProducedBlock(produceResult, request);
            return produceResult;
        }

        /// <summary>
        ///     The slot and name-pool capacities for a scan: the estimated plan, grown to twice the
        ///     drive's current block's capacities, each independently, when that block ran out. A
        ///     rescan of a tree larger than the estimate therefore plans a larger block instead of
        ///     repeating the exhausted one.
        /// </summary>
        (uint SlotCapacity, uint NamePoolCapacity) PlanCapacities(uint estimatedRows)
        {
            var slotCapacity = BlockLayout.ComputeSlotCapacity(estimatedRows);
            var namePoolCapacity = BlockLayout.ComputeNamePoolCapacity(EnumerationProducer.EstimateNamePoolBytes(estimatedRows));
            lock (StateLock)
            {
                if (Block?.Block.Header is { IsCapacityExhausted: true } exhausted)
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
        ///     Exhausted attempts are discarded and retried with geometrically larger capacities,
        ///     up to the layout's capacity clamp, before a block is completed and returned.
        /// </summary>
        BlockFile CreateAndPopulateBlock(string blockPath, bool deleteOnClose,
            EnumerationWalkLimit.Lease walkLease, CancellationToken cancellationToken)
        {
            var (slotCapacity, namePoolCapacity) = PlanCapacities(
                EnumerationProducer.EstimateRowCount(Configuration.RootDirectory));

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockFile? block = BlockFile.Create(new BlockFileCreateOptions
                {
                    Path = blockPath,
                    VolumeSerial = Configuration.VolumeSerial,
                    ProducerKind = ProducerKind.Enumeration,
                    SlotCapacity = slotCapacity,
                    NamePoolCapacity = namePoolCapacity,
                    DeleteOnClose = deleteOnClose,
                    Diagnostics = Options.Diagnostics,
                    CacheTag = Options.CacheTag,
                    ScanShape = ExpectedScanShape
                });
                try
                {
                    var writer = new BlockWriter(block);
                    var producer = new EnumerationProducer(new EnumerationProducerOptions
                    {
                        RootDirectory = Configuration.RootDirectory,
                        Drive = Drive,
                        RetainedFileNames = Options.RetainedFileNames,
                        DirectoryWalk = Options.DirectoryWalk
                    });

                    var result = producer.Produce(writer, Options.ScanProgress, walkLease, cancellationToken);
                    var nextSlotCapacity = BlockLayout.DoubleCapacity(slotCapacity);
                    var nextNamePoolCapacity = BlockLayout.DoubleCapacity(namePoolCapacity);
                    if (!result.CapacityExhausted ||
                        (nextSlotCapacity == slotCapacity && nextNamePoolCapacity == namePoolCapacity))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.Complete(Options.TimeProvider.GetUtcNow().UtcDateTime, null);
                        var completed = block;
                        block = null;
                        return completed;
                    }

                    slotCapacity = nextSlotCapacity;
                    namePoolCapacity = nextNamePoolCapacity;
                }
                finally
                {
                    block?.Dispose();
                }

                // Dispose first: private blocks delete on close; canonical attempts need an
                // explicit deletion while the caller still holds their cache owner lock.
                if (!deleteOnClose)
                {
                    File.Delete(blockPath);
                    Options.Diagnostics?.Invoke($"Deleted block file '{blockPath}': enumeration capacity exhausted; retrying with a larger block.");
                }
            }
        }

        static void ValidateProducedBlock(MftBlockProduceResult result, MftBlockProduceRequest request)
        {
            try
            {
                var header = result.Block.Header;
                if (header.RowCount == 0)
                {
                    throw new InvalidOperationException("Block RowCount must be greater than zero.");
                }

                var validation = BlockHeader.Validate(in header, request.VolumeSerial, result.Block.Length);
                if (validation is not null)
                {
                    throw new InvalidOperationException($"Block validation failed: {validation}.");
                }

                if (header.ProducerKind != ProducerKind.Mft)
                {
                    throw new InvalidOperationException("Block ProducerKind must be Mft.");
                }

                if (header.CacheTag != request.CacheTag)
                {
                    throw new InvalidOperationException(
                        $"The MFT producer's block carries cache tag {header.CacheTag}, but the request requires {request.CacheTag}.");
                }

                if (header.ScanShape != request.ScanShape)
                {
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"The MFT producer's block carries scan shape {header.ScanShape:X16}, but the request requires {request.ScanShape:X16}."));
                }
            }
            catch (Exception exception)
            {
                result.Block.Dispose();
                BlockFile.TryDeleteFailedCreate(request.BlockPath, request.Diagnostics,
                    $"the MFT producer's returned block failed validation: {exception.Message}");
                throw;
            }
        }
    }
}
