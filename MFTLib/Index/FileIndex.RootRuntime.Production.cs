namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class RootRuntime
    {
        /// <summary>
        ///     Picks the producer for one root's scan and returns what it produced, keyed by nothing:
        ///     the finished block with its access-denied count and its lost catch-up, or no block and
        ///     the selected producer's failure. Enumeration walks the directory tree; operational
        ///     failures from either policy are returned so only that root fails. Cancellation propagates.
        ///     The caller builds the <see cref="RootBlock" /> when it publishes.
        /// </summary>
        async Task<PendingRootResult> ProduceRootBlockCoreAsync(string blockPath,
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
                    return new PendingRootResult
                    {
                        Block = scannedBlock,
                        Origin = BlockOrigin.ProducedByScan
                    };
                }

                if (Options.MftSource is not { } source)
                {
                    return new PendingRootResult
                    {
                        ScanFailureKind = RootFailureKind.MftSourceMissing,
                        ProducerFailureMessage = $"Volume {Root}: no MFT source is available to scan this volume."
                    };
                }

                var producer = source.Producer;
                var produced = await RunMftProducerAsync(blockPath, deleteOnClose, producer, cancellationToken)
                    .ConfigureAwait(false);
                return new PendingRootResult
                {
                    Block = produced.Block,
                    Origin = BlockOrigin.ProducedByScan,
                    SkippedRecordCount = produced.SkippedRecordCount,
                    CatchUpLoss = produced.CatchUpLoss
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new PendingRootResult { ProducerFailure = exception, ProducerFailureMessage = exception.Message };
            }
        }

        /// <summary>
        ///     Runs the MFT producer for one volume and checks its finished block before anything adopts
        ///     it. The returned header contains the durable pre-scan cursor, independently validated
        ///     by the producer. <see cref="DescribeLocked" /> derives
        ///     <see cref="RootBlockStatus.IsCapacityExhausted" /> from the header's own
        ///     <see cref="BlockFlags.CapacityExhausted" /> flag, which the producer sets on the block directly.
        /// </summary>
        async Task<MftBlockProduceResult> RunMftProducerAsync(string blockPath,
            bool deleteOnClose, MftBlockProducer producer, CancellationToken cancellationToken)
        {
            var request = new MftBlockProduceRequest
            {
                Volume = Volume,
                VolumeSerial = Configuration.VolumeSerial,
                BlockPath = blockPath,
                DeleteOnClose = deleteOnClose,
                Progress = Options.ScanProgress,
                CacheTag = Options.CacheTag,
                ScanShape = ExpectedScanShape,
                Diagnostics = Options.Diagnostics
            };

            var produceResult = await producer(request, cancellationToken).ConfigureAwait(false);
            ValidateProducedBlock(produceResult, request);
            return produceResult;
        }

        /// <summary>
        ///     The slot and name-pool capacities for a scan: the estimated plan, grown to twice the
        ///     root's current block's capacities, each independently, when that block ran out. A
        ///     rescan of a tree larger than the estimate therefore plans a larger block instead of
        ///     repeating the exhausted one.
        /// </summary>
        (uint SlotCapacity, uint NamePoolCapacity) PlanCapacities(uint estimatedRows)
        {
            var slotCapacity = BlockLayout.ComputeSlotCapacity(estimatedRows);
            var namePoolCapacity = BlockLayout.ComputeNamePoolCapacity(slotCapacity);
            lock (StateLock)
            {
                if (Block?.Block.Header is { IsCapacityExhausted: true } exhausted)
                {
                    slotCapacity = Math.Max(slotCapacity, BlockLayout.DoubleCapacity(exhausted.SlotCapacity));
                    namePoolCapacity = Math.Max(namePoolCapacity, BlockLayout.DoubleNamePoolCapacity(exhausted.NamePoolCapacity));
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
        ///     A block that runs out of rows or name pool is grown while the walk continues: its rows
        ///     are copied out, the block is disposed and deleted, and a block with both capacities
        ///     doubled, up to the layout's capacity clamp, takes them back. No directory is read twice.
        /// </summary>
        BlockFile CreateAndPopulateBlock(string blockPath, bool deleteOnClose,
            EnumerationWalkLimit.Lease walkLease, CancellationToken cancellationToken)
        {
            var (slotCapacity, namePoolCapacity) = PlanCapacities(
                EnumerationProducer.EstimateRowCount(Configuration.RootDirectory));

            cancellationToken.ThrowIfCancellationRequested();
            using var growth = new EnumerationBlockGrowth(this, blockPath, deleteOnClose, slotCapacity, namePoolCapacity);
            var producer = new EnumerationProducer(new EnumerationProducerOptions
            {
                RootDirectory = Configuration.RootDirectory,
                Root = Root,
                DirectoryWalk = Options.DirectoryWalk
            })
            {
                GrowWriter = growth.Grow
            };

            producer.Produce(new BlockWriter(growth.Block), Options.ScanProgress, walkLease, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            new BlockWriter(growth.Block).Complete(Options.TimeProvider.GetUtcNow().UtcDateTime, null);
            return growth.Release();
        }

        BlockFile CreateEnumerationBlock(string blockPath, bool deleteOnClose, uint slotCapacity,
            uint namePoolCapacity) =>
            BlockFile.Create(new BlockFileCreateOptions
            {
                Path = blockPath,
                Root = Root,
                VolumeSerial = Configuration.VolumeSerial,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = slotCapacity,
                NamePoolCapacity = namePoolCapacity,
                DeleteOnClose = deleteOnClose,
                Diagnostics = Options.Diagnostics,
                CacheTag = Options.CacheTag,
                ScanShape = ExpectedScanShape
            });

        /// <summary>
        ///     Owns the enumeration block while a walk fills it. <see cref="Grow" /> replaces an exhausted
        ///     block with one twice as large holding the same rows; disposal releases whatever block is
        ///     current unless <see cref="Release" /> handed it to the caller.
        /// </summary>
        sealed class EnumerationBlockGrowth(RootRuntime runtime, string blockPath, bool deleteOnClose,
            uint slotCapacity, uint namePoolCapacity) : IDisposable
        {
            uint _slotCapacity = slotCapacity;
            uint _namePoolCapacity = namePoolCapacity;
            BlockFile? _block = runtime.CreateEnumerationBlock(blockPath, deleteOnClose, slotCapacity, namePoolCapacity);

            public BlockFile Block => _block ?? throw new ObjectDisposedException(nameof(EnumerationBlockGrowth));

            /// <summary>
            ///     Copies the exhausted block's rows out, disposes and deletes it, and restores the rows into a
            ///     block with both capacities doubled. Null when the layout's capacity clamp allows no growth.
            /// </summary>
            public BlockWriter? Grow(BlockWriter exhausted)
            {
                var nextSlotCapacity = BlockLayout.DoubleCapacity(_slotCapacity);
                var nextNamePoolCapacity = BlockLayout.DoubleNamePoolCapacity(_namePoolCapacity);
                if (nextSlotCapacity == _slotCapacity && nextNamePoolCapacity == _namePoolCapacity)
                {
                    return null;
                }

                var snapshot = BlockRowSnapshot.Capture(exhausted.Block);
                // Dispose first: private blocks delete on close; canonical blocks need an
                // explicit deletion while the caller still holds their cache owner lock.
                Dispose();
                if (!deleteOnClose)
                {
                    File.Delete(blockPath);
                }

                _slotCapacity = nextSlotCapacity;
                _namePoolCapacity = nextNamePoolCapacity;
                _block = runtime.CreateEnumerationBlock(blockPath, deleteOnClose, _slotCapacity, _namePoolCapacity);
                snapshot.RestoreInto(_block);
                DiagnosticsReport.Send(runtime.Options.Diagnostics,
                    $"Grew block file '{blockPath}': enumeration capacity exhausted at {snapshot.RowCount} rows; " +
                    $"the walk continues with {_slotCapacity} slots and {_namePoolCapacity} name-pool bytes.");
                return new BlockWriter(_block);
            }

            /// <summary>Hands the current block to the caller, who owns it from here.</summary>
            public BlockFile Release()
            {
                var released = Block;
                _block = null;
                return released;
            }

            public void Dispose()
            {
                _block?.Dispose();
                _block = null;
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
