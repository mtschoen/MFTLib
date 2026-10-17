namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        /// Runs one admitted rescan under this drive's lifecycle gate. Production keeps the current
        /// watch; publication commits its replacement and hands off teardown before an eligible restart.
        /// See docs/watch-lifetime.md for publication, stop-wins and cancellation ordering.
        /// </summary>
        public async Task RescanDriveAsync(CancellationToken cancellationToken)
        {
            if (_index.RejectInsideHandler(nameof(FileIndex.RescanAsync)) is { } rejection)
            {
                throw rejection;
            }

            ObjectDisposedException.ThrowIf(IsDisposed, _index);
            using var rescanCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposalToken);
            await LifecycleGate.WaitAsync(rescanCancellation.Token).ConfigureAwait(false);
            try
            {
                _index.ThrowIfCancelledByDisposal(rescanCancellation.Token);
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
        /// <param name="ProducerFailureMessage">What the drive's status reports for that failure.</param>
        /// <param name="CatchUpLoss">Set when the published block's catch-up was lost.</param>
        /// <param name="ConsecutiveLostCatchUps">The drive's count after a publish.</param>
        readonly record struct ScanAttempt(
            bool Published,
            Exception? ProducerFailure = null,
            string? ProducerFailureMessage = null,
            JournalCheckpointLoss? CatchUpLoss = null,
            int ConsecutiveLostCatchUps = 0);

        /// <summary>
        ///     One attempt of a scan operation: produces a block for the drive and publishes it, or,
        ///     when the producer returns none, records the producer's failure against the drive and
        ///     publishes nothing. A drive with a block has its canonical file renamed aside first and
        ///     restored whenever nothing replaces it.
        /// </summary>
        async Task<ScanAttempt> ScanAndPublishAsync(bool clearsCheckpointLoss,
            RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            _index.ThrowIfCancelledByDisposal(cancellationToken);
            var superseded = FindBlockForRescan();

            // The rename-aside is licensed by the owner lock: an index that does not hold it (its
            // block came from a private scan while another index owned the slot) rescans privately
            // again and never touches the canonical file.
            var ownsCanonicalSlot = Options.CacheMode != CacheMode.Disabled && EnsureCanonicalOwnership();
            var target = ComputeScanTarget(ownsCanonicalSlot);
            var retired = RenameAsideForRescan(target, superseded);

            PendingDriveResult produced;
            try
            {
                produced = await ProduceDriveBlockAsync(target.Path, target.DeleteOnClose, cancellationToken)
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
                var failureMessage = produced.RequireFailureMessage(Drive);
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
        ///     The drive's published block, or null for a drive that has none because a cache-only
        ///     open declined it or its producer failed. An offline drive cannot be rescanned.
        /// </summary>
        DriveBlock? FindBlockForRescan()
        {
            lock (StateLock)
            {
                if (Block is { } block)
                {
                    return block;
                }

                if (BlocklessState == DriveState.Failed)
                {
                    return null;
                }

                // ReSharper disable once NotResolvedInText
                throw new ArgumentException($"Drive {Drive} has no block.", "drive");
            }
        }

        /// <summary>
        ///     Records a rescan's producer failure on the drive's runtime, which then reads
        ///     <see cref="DriveFailureKind.ProducerFailed" />.
        /// </summary>
        void RecordRescanProducerFailure(PendingDriveResult failed)
        {
            lock (StateLock)
            {
                Failure = new DriveFailure(DriveFailureKind.ProducerFailed,
                    failed.RequireFailureMessage(Drive));
                if (Block is null)
                {
                    LastResult = failed;
                    BlocklessState = DriveState.Failed;
                    ReleaseCanonicalOwnershipLocked();
                }
            }
        }
    }
}
