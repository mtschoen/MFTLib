namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
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
                if (Resolved.Producer == ProducerKind.Enumeration)
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

                // The producer is fixed at open, so a root the MFT cannot serve fails here, on every attempt, and
                // never walks. MftOnly over an ineligible volume is the only way Resolved carries a reason.
                if (Resolved.MftUnavailable is { } ineligible)
                {
                    return DescribeUnavailable(ineligible);
                }

                if (Options.MftSource is not { } source)
                {
                    return DescribeUnavailable(MftUnavailableReason.NoMftSource);
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
            catch (MftUnavailableException exception)
            {
                return new PendingRootResult
                {
                    ScanFailureKind = RootFailureKind.MftUnavailable,
                    MftUnavailable = exception.Reason,
                    ProducerFailure = exception,
                    ProducerFailureMessage = exception.Message
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new PendingRootResult { ProducerFailure = exception, ProducerFailureMessage = exception.Message };
            }
        }

        /// <summary>The blockless result of an attempt the MFT could not serve because of a volume fact or a missing source.</summary>
        PendingRootResult DescribeUnavailable(MftUnavailableReason reason) => new()
        {
            ScanFailureKind = RootFailureKind.MftUnavailable,
            MftUnavailable = reason,
            ProducerFailureMessage = reason switch
            {
                MftUnavailableReason.NoMftSource => $"Volume {Root}: no MFT source is available to scan this volume.",
                MftUnavailableReason.PlatformWithoutMft =>
                    $"Root {Root}: the MFT is read on Windows only (PlatformWithoutMft) and FileIndexOptions.Producer is MftOnly.",
                _ => $"Root {Root}: the MFT cannot serve this root ({reason}) and FileIndexOptions.Producer is MftOnly."
            }
        };

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
                VolumeSerial = Resolved.VolumeSerial,
                BlockPath = blockPath,
                DeleteOnClose = deleteOnClose,
                MinimumCapacities = GrowthMinimumLocked(),
                Progress = this,
                CacheTag = Options.CacheTag,
                ScanShape = ExpectedScanShape,
                Diagnostics = Diagnostics
            };

            var produceResult = await producer(request, cancellationToken).ConfigureAwait(false);
            ValidateProducedBlock(produceResult, request);
            return produceResult;
        }

        /// <summary>Derives the replacement's regional minima from the adopted block under the state lock.</summary>
        BlockCapacities GrowthMinimumLocked()
        {
            lock (StateLock)
            {
                return Block is null ? default : BlockLayout.GrowthMinimum(in Block.Block.Header);
            }
        }

        /// <summary>
        ///     Creates a fresh block at <paramref name="blockPath" /> and runs the enumeration
        ///     producer to completion. If the producer throws for any reason, including
        ///     cancellation, the partially written block is disposed (deleting its file when
        ///     <paramref name="deleteOnClose" /> is set) before the exception propagates, so a failed
        ///     or cancelled scan never leaves an unreachable mapping, and a no-cache attempt never
        ///     leaves an orphaned temp file, behind. The returned <see cref="BlockFile" /> is the
        ///     caller's to own from here.
        ///     A block that runs out of rows, name entries or name pool is grown while the walk continues:
        ///     only its exhausted regions double, and no directory is read twice.
        /// </summary>
        BlockFile CreateAndPopulateBlock(string blockPath, bool deleteOnClose,
            EnumerationWalkLimit.Lease walkLease, CancellationToken cancellationToken)
        {
            var capacities = BlockLayout.PlanCapacities(
                EnumerationProducer.EstimateRowCount(Resolved.RootDirectory), GrowthMinimumLocked());

            cancellationToken.ThrowIfCancellationRequested();
            using var growth = new EnumerationBlockGrowth(this, blockPath, deleteOnClose, capacities);
            var producer = new EnumerationProducer(new EnumerationProducerOptions
            {
                RootDirectory = Resolved.RootDirectory,
                Root = BlockRoot,
                DirectoryWalk = Options.DirectoryWalk
            })
            {
                GrowWriter = growth.Grow
            };

            producer.Produce(new BlockWriter(growth.Block), this, walkLease, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            new BlockWriter(growth.Block).Complete(Options.TimeProvider.GetUtcNow().UtcDateTime, null, cancellationToken: cancellationToken);
            return growth.Release();
        }

        BlockFile CreateEnumerationBlock(string blockPath, bool deleteOnClose, BlockCapacities capacities) =>
            BlockFile.Create(new BlockFileCreateOptions
            {
                Path = blockPath,
                Root = BlockRoot,
                VolumeSerial = Resolved.VolumeSerial,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = capacities.SlotCapacity,
                NameEntryCapacity = capacities.NameEntryCapacity,
                NamePoolCapacity = capacities.NamePoolCapacity,
                DeltaCapacity = capacities.DeltaCapacity,
                DeleteOnClose = deleteOnClose,
                Diagnostics = Diagnostics,
                CacheTag = Options.CacheTag,
                ScanShape = ExpectedScanShape
            });

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
