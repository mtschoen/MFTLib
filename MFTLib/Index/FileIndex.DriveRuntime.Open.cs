namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Settles one drive while opening: warm-starts it from its cached block or scans it into a
        ///     <see cref="PendingDriveResult" /> that nothing shared has seen yet, then adopts that result
        ///     under <see cref="_stateLock" />, which is where the drive's ordinal is assigned. A drive
        ///     that ends with no block is recorded by letter as blockless instead.
        /// </summary>
        public async Task<int> AddDriveAsync(OpenSettlement settlement, CancellationToken cancellationToken)
        {
            var driveKey = Drive;
            if (DriveKind.For(Configuration.Producer, Options.MftSource).RequiresExistingRoot &&
                !Directory.Exists(Configuration.RootDirectory))
            {
                return RecordOfflineDrive(settlement);
            }

            var slotOutcome = ResolveCanonicalOwnership();
            if (slotOutcome == CanonicalSlotOutcome.DeclinedInUse)
            {
                return RecordFailedDrive(DriveFailureKind.InUse, new PendingDriveResult
                {
                    ProducerFailureMessage =
                        $"Drive {driveKey}: cache block is in use by another FileIndex and --cache-only forbids a scan."
                }, settlement);
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
                var (_, _, settledCount) = AdoptOpenedDrive(warmStartedBlock, opened with
                {
                    Block = warmStartedBlock,
                    Origin = BlockOrigin.WarmStartedFromCache,
                    CacheOnlyUnresumable = warmStart.CacheOnlyUnresumable
                }, settlement);
                return settledCount ?? throw new InvalidOperationException($"Drive {driveKey} settled without its count.");
            }

            if (Options.CacheMode == CacheMode.ExistingOnly)
            {
                var failureKind = warmStart.DiscardedBlock is BlockValidationResult.WrongCacheTag
                    or BlockValidationResult.WrongScanShape
                    ? DriveFailureKind.CacheIdentityMismatch
                    : DriveFailureKind.CacheDeclined;
                return RecordFailedDrive(failureKind, opened with
                {
                    ProducerFailureMessage =
                        $"Drive {driveKey}: no usable cache (missing, corrupt, or incompatible) and --cache-only forbids a scan."
                }, settlement);
            }

            return await ScanOpenedDriveAsync(ownsCanonicalSlot, opened, settlement, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///     Adopts an opened drive's settled result: assigns its ordinal (the one of
        ///     <paramref name="replacing" />, an unpublished block an earlier attempt of the same settle
        ///     adopted, when there is one), builds its <see cref="DriveBlock" /> over
        ///     <paramref name="block" />, and records the result on the drive's runtime in
        ///     one step under <see cref="_stateLock" />; the adoption that ends the drive's settle, one
        ///     that held its catch-up or reached the limit, also claims the drive's settle count. The
        ///     snapshot is published once every drive has settled. Returns the adopted block and the drive's count of lost catch-ups in a row.
        /// </summary>
        (DriveBlock Adopted, int ConsecutiveLostCatchUps, int? SettledCount) AdoptOpenedDrive(
            BlockFile block, PendingDriveResult settled, OpenSettlement settlement, DriveBlock? replacing = null)
        {
            lock (StateLock)
            {
                var driveOrdinal = replacing?.DriveOrdinal ?? _index.AllocateDriveOrdinalLocked();
                var adopted = BuildDriveBlock(driveOrdinal, block);
                Block = adopted;

                RecordPendingResultLocked(settled, clearsCheckpointLoss: false);
                int? settledCount = settled.CatchUpLoss is null ||
                    _consecutiveLostCatchUps >= LostCatchUpRecoveryLimit
                    ? settlement.ClaimLocked()
                    : null;

                return (adopted, _consecutiveLostCatchUps, settledCount);
            }
        }

        int RecordOfflineDrive(OpenSettlement settlement)
        {
            lock (StateLock)
            {
                var settledCount = settlement.ClaimLocked();
                BlocklessState = DriveState.Offline;
                Block = null;
                _lastResult = null;
                _failure = null;
                return settledCount;
            }
        }

        /// <summary>
        ///     Records a drive that opening left with no block, from its settled result and keyed by
        ///     drive, since a drive that never adds a block never takes an ordinal.
        /// </summary>
        int RecordFailedDrive(DriveFailureKind failureKind, PendingDriveResult settled,
            OpenSettlement settlement)
        {
            lock (StateLock)
            {
                var settledCount = settlement.ClaimLocked();
                BlocklessState = DriveState.Failed;
                _lastResult = settled;
                _failure = new DriveFailure(failureKind, settled.RequireFailureMessage(Drive));
                _checkpointLoss = settled.CheckpointLoss;
                ReleaseCanonicalOwnershipLocked();
                return settledCount;
            }
        }

        int RecordOpenProducerFailure(PendingDriveResult failed, bool lostBlockInPlace,
            OpenSettlement settlement)
        {
            if (lostBlockInPlace)
            {
                lock (StateLock)
                {
                    RecordRescanProducerFailure(failed);
                    return settlement.ClaimLocked();
                }
            }

            return RecordFailedDrive(failed.ScanFailureKind, failed, settlement);
        }
    }
}
