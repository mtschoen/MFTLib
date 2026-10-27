namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Settles the root while opening: warm-starts it from its cached block or scans it into a
        ///     <see cref="PendingDriveResult" /> that nothing shared has seen yet, then adopts that result
        ///     under <see cref="_stateLock" />. A root that ends with no block is recorded as blockless instead.
        /// </summary>
        public async Task AddDriveAsync(CancellationToken cancellationToken)
        {
            var driveKey = Drive;
            if (DriveKind.For(Configuration.Producer, Options.MftSource).RequiresExistingRoot &&
                !Directory.Exists(Configuration.RootDirectory))
            {
                RecordOfflineDrive();
                return;
            }

            var slotOutcome = ResolveCanonicalOwnership();
            if (slotOutcome == CanonicalSlotOutcome.DeclinedInUse)
            {
                RecordFailedDrive(DriveFailureKind.InUse, new PendingDriveResult
                {
                    ProducerFailureMessage =
                        $"Drive {driveKey}: cache block is in use by another FileIndex and CacheMode.ExistingOnly forbids a scan."
                });
                return;
            }

            var ownsCanonicalSlot = slotOutcome == CanonicalSlotOutcome.Owned;
            var warmStart = ownsCanonicalSlot
                ? TryOpenExistingBlock()
                : new WarmStartResult(null, null);
            warmStart = RejectUnresumableCheckpoint(warmStart);
            var opened = new PendingDriveResult
            {
                CheckpointLoss = warmStart.CheckpointLoss,
                CacheLocation = DescribeCacheLocation(ownsCanonicalSlot)
            };

            if (warmStart.Block is { } warmStartedBlock)
            {
                _ = AdoptOpenedDrive(warmStartedBlock, opened with
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
                    ? DriveFailureKind.CacheIdentityMismatch
                    : DriveFailureKind.CacheDeclined;
                RecordFailedDrive(failureKind, opened with
                {
                    ProducerFailureMessage =
                        $"Drive {driveKey}: no usable cache (missing, corrupt, or incompatible) and CacheMode.ExistingOnly forbids a scan."
                });
                return;
            }

            await ScanOpenedDriveAsync(ownsCanonicalSlot, opened, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///     Adopts an opened root's settled result: builds its <see cref="DriveBlock" /> over
        ///     <paramref name="block" /> and records the result on the runtime in one step under
        ///     <see cref="_stateLock" />. Returns the adopted block and the count of lost catch-ups in a row.
        /// </summary>
        (DriveBlock Adopted, int ConsecutiveLostCatchUps) AdoptOpenedDrive(
            BlockFile block, PendingDriveResult settled, DriveBlock? replacing = null)
        {
            lock (StateLock)
            {
                if (replacing is not null)
                {
                    new BlockWriter(block).AdvanceGenerationPast(replacing.Block.Generation);
                }

                var adopted = BuildDriveBlock(block);
                Block = adopted;

                RecordPendingResultLocked(settled, clearsCheckpointLoss: false);
                return (adopted, _consecutiveLostCatchUps);
            }
        }

        void RecordOfflineDrive()
        {
            lock (StateLock)
            {
                BlocklessState = DriveState.Offline;
                Block = null;
                _lastResult = null;
                _failure = null;
            }
        }

        /// <summary>
        ///     Records a root that opening left with no block.
        /// </summary>
        void RecordFailedDrive(DriveFailureKind failureKind, PendingDriveResult settled)
        {
            lock (StateLock)
            {
                BlocklessState = DriveState.Failed;
                _lastResult = settled;
                _failure = new DriveFailure(failureKind, settled.RequireFailureMessage(Drive));
                _checkpointLoss = settled.CheckpointLoss;
                ReleaseCanonicalOwnershipLocked();
            }
        }

        void RecordOpenProducerFailure(PendingDriveResult failed, bool lostBlockInPlace)
        {
            if (lostBlockInPlace)
            {
                lock (StateLock)
                {
                    RecordRescanProducerFailure(failed);
                }

                return;
            }

            RecordFailedDrive(failed.ScanFailureKind, failed);
        }
    }
}
