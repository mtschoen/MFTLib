namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     Settles the root while opening: warm-starts it from its cached block or scans it into a
        ///     <see cref="PendingRootResult" /> that nothing shared has seen yet, then adopts that result
        ///     under <see cref="StateLock" />. A root that ends with no block is recorded as blockless instead.
        ///     A root missing at open settles Offline before selection, except that an MftOnly open on a platform without
        ///     an MFT fails first with that reason. An <see cref="ProducerSelection.Automatic" />
        ///     open whose cold scan finds the MFT unusable (no source, no broker, elevation declined) settles once
        ///     more as a walk: the swap releases the MFT cache slot first, because a directory root's walk is named
        ///     differently from the volume block, and it happens only here, before the index is returned.
        /// </summary>
        public async Task AddRootAsync(CancellationToken cancellationToken)
        {
            // A platform without an MFT is a fact about the host, not about the root: an MftOnly open reports it
            // even though the root's host path (a drive letter off Windows) does not exist there.
            if (Resolved is { Producer: ProducerKind.Mft, MftUnavailable: MftUnavailableReason.PlatformWithoutMft })
            {
                RecordFailedRoot(RootFailureKind.MftUnavailable, DescribeUnavailable(MftUnavailableReason.PlatformWithoutMft));
                return;
            }

            if (!Resolved.IsDump && !Directory.Exists(Resolved.RootDirectory))
            {
                RecordOfflineRoot();
                return;
            }

            if (Resolved is { Producer: ProducerKind.Mft, MftUnavailable: { } ineligible })
            {
                RecordFailedRoot(RootFailureKind.MftUnavailable, DescribeUnavailable(ineligible));
                return;
            }

            if (await SettleResolvedRootAsync(cancellationToken).ConfigureAwait(false) is { } fallback)
            {
                Resolved = Resolved.AsWalk(fallback);
                foreach (var view in ViewsSnapshot())
                {
                    view.Resolved = view.Resolved.AsWalk(fallback);
                }
                lock (StateLock)
                {
                    // Nothing was published by the MFT attempt; its recorded outcome gives way to the walk's.
                    _failure = null;
                    _checkpointLoss = null;
                    _lastResult = null;
                }

                // A walk never asks for a fallback, so this settle is the last.
                _ = await SettleResolvedRootAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     One settle of the resolved root. Returns the reason to fall back to a walk, or null when the root
        ///     settled (with a block, or failed, or declined).
        /// </summary>
        async Task<MftUnavailableReason?> SettleResolvedRootAsync(CancellationToken cancellationToken)
        {
            var rootKey = Root;

            var slotOutcome = ResolveCanonicalOwnership();
            if (slotOutcome == CanonicalSlotOutcome.DeclinedInUse)
            {
                RecordFailedRoot(RootFailureKind.InUse, new PendingRootResult
                {
                    ProducerFailureMessage =
                        $"Root {rootKey}: cache block is in use by another FileIndex and CacheMode.ExistingOnly forbids a scan."
                });
                return null;
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
                _ = await AdoptOpenedRootAsync(warmStartedBlock, opened with
                {
                    Block = warmStartedBlock,
                    Origin = BlockOrigin.WarmStartedFromCache,
                    CacheOnlyUnresumable = warmStart.CacheOnlyUnresumable
                }).ConfigureAwait(false);
                return null;
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
                return null;
            }

            // A missing source is judged here and only here at open: the open has to produce, and a cached block
            // would already have been adopted above.
            if (Resolved.Producer == ProducerKind.Mft && Options.MftSource is null)
            {
                if (Options.Producer == ProducerSelection.Automatic)
                {
                    lock (StateLock)
                    {
                        ReleaseCanonicalOwnershipLocked();
                    }

                    return MftUnavailableReason.NoMftSource;
                }

                RecordFailedRoot(RootFailureKind.MftUnavailable,
                    DescribeUnavailable(MftUnavailableReason.NoMftSource) with
                    {
                        CheckpointLoss = opened.CheckpointLoss,
                        CacheLocation = opened.CacheLocation
                    });
                return null;
            }

            await ScanOpenedRootAsync(ownsCanonicalSlot, opened, cancellationToken).ConfigureAwait(false);
            return TakeFallbackRequest();
        }

        /// <summary>
        ///     The reason to settle the open again as a walk: an Automatic open whose cold scan ended with no block
        ///     because the MFT could not be used. Anything else, a failed producer included, stays as recorded.
        /// </summary>
        MftUnavailableReason? TakeFallbackRequest()
        {
            lock (StateLock)
            {
                return Block is null && _failure is { Kind: RootFailureKind.MftUnavailable } &&
                       Options.Producer == ProducerSelection.Automatic && Resolved.Producer == ProducerKind.Mft
                    ? _lastMftFailure
                    : null;
            }
        }

        /// <summary>
        ///     Adopts an opened root's settled result: builds its <see cref="RootBlock" /> over
        ///     <paramref name="block" /> and records the result on the runtime in one step under
        ///     <see cref="StateLock" />. Returns the adopted block and the count of lost catch-ups in a row.
        /// </summary>
        async Task<(RootBlock Adopted, int ConsecutiveLostCatchUps)> AdoptOpenedRootAsync(
            BlockFile block, PendingRootResult settled, RootBlock? replacing = null)
        {
            await WriteGate.WaitAsync(ClosingToken).ConfigureAwait(false);
            try
            {
                if (replacing is not null)
                {
                    new BlockWriter(block).AdvanceGenerationPast(replacing.Block.Generation);
                }

                var adopted = BuildRootBlock(block);
                var bindings = BindViews(adopted.Mapped);
                lock (StateLock)
                {
                    PublishBindingsLocked(adopted, bindings);
                    RecordPendingResultLocked(settled, clearsCheckpointLoss: false);
                    return (adopted, _consecutiveLostCatchUps);
                }
            }
            finally
            {
                WriteGate.Release();
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
                _lastMftFailure = failureKind == RootFailureKind.MftUnavailable ? settled.MftUnavailable : null;
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
