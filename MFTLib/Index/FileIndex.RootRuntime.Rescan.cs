namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class RootRuntime
    {
        /// <summary>
        /// Runs one admitted rescan under this root's lifecycle gate. Production keeps the current
        /// watch; publication commits its replacement and hands off teardown before an eligible restart.
        /// See docs/watch-lifetime.md for publication, stop-wins and cancellation ordering.
        /// </summary>
        public async Task RescanRootAsync(CancellationToken cancellationToken)
        {
            if (FileIndex.RejectInsideHandler(nameof(RescanAsync)) is { } rejection)
            {
                throw rejection;
            }

            ObjectDisposedException.ThrowIf(IsDisposed, Index);
            using var rescanCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposalToken);
            await LifecycleGate.WaitAsync(rescanCancellation.Token).ConfigureAwait(false);
            try
            {
                Index.ThrowIfCancelledByDisposal(rescanCancellation.Token);
                await RescanWithGateHeldAsync(recovery: null, rescanCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                LifecycleGate.Release();
            }
        }

        /// <summary>What one attempt of a scan operation did.</summary>
        /// <param name="Published">False when the producer returned no block, so nothing was published.</param>
        /// <param name="ProducerFailure">The producer's failure when it returned no block.</param>
        /// <param name="ProducerFailureMessage">What the root's status reports for that failure.</param>
        /// <param name="CatchUpLoss">Set when the published block's catch-up was lost.</param>
        /// <param name="ConsecutiveLostCatchUps">The root's count after a publish.</param>
        readonly record struct ScanAttempt(
            bool Published,
            Exception? ProducerFailure = null,
            string? ProducerFailureMessage = null,
            JournalCheckpointLoss? CatchUpLoss = null,
            int ConsecutiveLostCatchUps = 0);

        /// <summary>
        ///     One attempt of a scan operation: produces a block for the root and publishes it, or,
        ///     when the producer returns none, records the producer's failure against the root and
        ///     publishes nothing. A root with a block has its canonical file renamed aside first and
        ///     restored whenever nothing replaces it.
        /// </summary>
        async Task<ScanAttempt> ScanAndPublishAsync(bool clearsCheckpointLoss,
            RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            Index.ThrowIfCancelledByDisposal(cancellationToken);
            var superseded = FindBlockForRescan();

            // The rename-aside is licensed by the owner lock: an index that does not hold it (its
            // block came from a private scan while another index owned the slot) rescans privately
            // again and never touches the canonical file.
            var ownsCanonicalSlot = Options.CacheMode != CacheMode.Disabled && EnsureCanonicalOwnership();
            var target = ComputeScanTarget(ownsCanonicalSlot);
            var retired = RenameAsideForRescan(target, superseded);

            PendingRootResult produced;
            try
            {
                produced = await ProduceRootBlockAsync(target.Path, target.DeleteOnClose, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                DiscardUnproducedTarget(target, retired);
                throw;
            }

            if (produced.Block is not { } block)
            {
                DiscardUnproducedTarget(target, retired);
                var failureMessage = produced.RequireFailureMessage(Root);
                RecordRescanProducerFailure(produced);
                return new ScanAttempt(Published: false, produced.ProducerFailure, failureMessage);
            }

            (int ConsecutiveLostCatchUps, WatchHandoff Handoff) publication;
            try
            {
                publication = await PublishRescannedBlockAsync(block,
                    produced with { CacheLocation = DescribeCacheLocation(target.OwnsCanonicalSlot) }, clearsCheckpointLoss,
                    recovery, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Never published, so it owns no references; its mapping is closed directly.
                block.Dispose();
                RestoreRetiredFile(retired);
                throw;
            }

            // Ownership has passed to the snapshot. Teardown cannot enter the unpublished-block cleanup.
            await publication.Handoff.DrainAsync().ConfigureAwait(false);
            return new ScanAttempt(Published: true, CatchUpLoss: produced.CatchUpLoss,
                ConsecutiveLostCatchUps: publication.ConsecutiveLostCatchUps);
        }

        /// <summary>
        ///     The root's published block, or null for a root that has none because a cache-only
        ///     open declined it or its producer failed. An offline root cannot be rescanned.
        /// </summary>
        RootBlock? FindBlockForRescan()
        {
            lock (StateLock)
            {
                if (Block is { } block)
                {
                    return block;
                }

                if (BlocklessState == RootState.Failed)
                {
                    return null;
                }

                // ReSharper disable once NotResolvedInText
                throw new ArgumentException($"Root {Root} has no block.", "root");
            }
        }

        /// <summary>
        ///     Records a rescan's scan failure kind on the root's runtime, retaining the previous
        ///     block when present.
        /// </summary>
        void RecordRescanProducerFailure(PendingRootResult failed)
        {
            lock (StateLock)
            {
                _failure = new RootFailure(failed.ScanFailureKind,
                    failed.RequireFailureMessage(Root));
                _lastMftFailure = failed.ScanFailureKind == RootFailureKind.MftUnavailable ? failed.MftUnavailable : null;
                if (Block is null)
                {
                    _lastResult = failed;
                    BlocklessState = RootState.Failed;
                    ReleaseCanonicalOwnershipLocked();
                }
            }
        }
    }
}
