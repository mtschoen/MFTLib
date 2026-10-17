namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Rebuilds one drive's block into a new file and swaps it into the current snapshot.
        ///     For a cache-mode drive, the file at the canonical path is renamed aside first (safe
        ///     even while it is still mapped, since the block's memory-mapped file opens with
        ///     <see cref="FileShare.Delete" />) so the new block can take the canonical name while a
        ///     handle from the retired snapshot keeps reading the renamed file until that snapshot is
        ///     released. The rename only happens while this index holds the canonical path's owner
        ///     lock; a block another index owns is never renamed. If the scan fails or is cancelled,
        ///     the previous in-memory block is left in place and the renamed-aside file is moved
        ///     straight back to restore the on-disk cache.
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
                await _index.RescanWithGateHeldAsync(this, Configuration, recovery: null, rescanCancellation.Token)
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
        public readonly record struct ScanAttempt(
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
