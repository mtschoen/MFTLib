namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class RootRuntime
    {
        /// <summary>
        ///     Settles the root while opening: warm-starts it from its cached block or scans it into a
        ///     <see cref="PendingRootResult" /> that nothing shared has seen yet, then adopts that result
        ///     under <see cref="_stateLock" />. A root that ends with no block is recorded as blockless instead.
        /// </summary>
        public async Task AddRootAsync(CancellationToken cancellationToken)
        {
            var rootKey = Root;
            if (RootKind.For(Configuration.Producer, Options.MftSource).RequiresExistingRoot &&
                !Directory.Exists(Configuration.RootDirectory))
            {
                RecordOfflineRoot();
                return;
            }

            var slotOutcome = ResolveCanonicalOwnership();
            if (slotOutcome == CanonicalSlotOutcome.DeclinedInUse)
            {
                RecordFailedRoot(RootFailureKind.InUse, new PendingRootResult
                {
                    ProducerFailureMessage =
                        $"Root {rootKey}: cache block is in use by another FileIndex and CacheMode.ExistingOnly forbids a scan."
                });
                return;
            }

            var ownsCanonicalSlot = slotOutcome == CanonicalSlotOutcome.Owned;
            var warmStart = ownsCanonicalSlot
                ? TryOpenExistingBlock()
                : new WarmStartResult(null, null);
            warmStart = RejectUnresumableCheckpoint(warmStart);
            var opened = new PendingRootResult
            {
                CheckpointLoss = warmStart.CheckpointLoss,
                CacheLocation = DescribeCacheLocation(ownsCanonicalSlot)
            };

            if (warmStart.Block is { } warmStartedBlock)
            {
                _ = AdoptOpenedRoot(warmStartedBlock, opened with
                {
                    Block = warmStartedBlock,
                    Origin = BlockOrigin.WarmStartedFromCache,
                    CacheOnlyUnresumable = warmStart.CacheOnlyUnresumable
                });
                return;
            }

            if (Options.CacheMode == CacheMode.ExistingOnly)
            {
                var failureKind = warmStart.DiscardedBlock is BlockValidationResult.WrongCacheTag
                    or BlockValidationResult.WrongScanShape
                    ? RootFailureKind.CacheIdentityMismatch
                    : RootFailureKind.CacheDeclined;
                RecordFailedRoot(failureKind, opened with
                {
                    ProducerFailureMessage =
                        $"Root {rootKey}: no usable cache (missing, corrupt, or incompatible) and CacheMode.ExistingOnly forbids a scan."
                });
                return;
            }

            await ScanOpenedRootAsync(ownsCanonicalSlot, opened, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///     Adopts an opened root's settled result: builds its <see cref="RootBlock" /> over
        ///     <paramref name="block" /> and records the result on the runtime in one step under
        ///     <see cref="_stateLock" />. Returns the adopted block and the count of lost catch-ups in a row.
        /// </summary>
        (RootBlock Adopted, int ConsecutiveLostCatchUps) AdoptOpenedRoot(
            BlockFile block, PendingRootResult settled, RootBlock? replacing = null)
        {
            lock (StateLock)
            {
                if (replacing is not null)
                {
                    new BlockWriter(block).AdvanceGenerationPast(replacing.Block.Generation);
                }

                var adopted = BuildRootBlock(block);
                Block = adopted;

                RecordPendingResultLocked(settled, clearsCheckpointLoss: false);
                return (adopted, _consecutiveLostCatchUps);
            }
        }

        void RecordOfflineRoot()
        {
            lock (StateLock)
            {
                BlocklessState = RootState.Offline;
                Block = null;
                _lastResult = null;
                _failure = null;
            }
        }

        /// <summary>
        ///     Records a root that opening left with no block.
        /// </summary>
        void RecordFailedRoot(RootFailureKind failureKind, PendingRootResult settled)
        {
            lock (StateLock)
            {
                BlocklessState = RootState.Failed;
                _lastResult = settled;
                _failure = new RootFailure(failureKind, settled.RequireFailureMessage(Root));
                _checkpointLoss = settled.CheckpointLoss;
                ReleaseCanonicalOwnershipLocked();
            }
        }

        void RecordOpenProducerFailure(PendingRootResult failed, bool lostBlockInPlace)
        {
            if (lostBlockInPlace)
            {
                lock (StateLock)
                {
                    RecordRescanProducerFailure(failed);
                }

                return;
            }

            RecordFailedRoot(failed.ScanFailureKind, failed);
        }
    }
}
