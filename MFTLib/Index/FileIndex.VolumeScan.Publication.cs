namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     The block's ownership passes to the <see cref="RootBlock" />, which releases it through
        ///     <see cref="RootBlock.ReleaseState" /> (see its own summary), not through
        ///     <see cref="IDisposable" />.
        /// </summary>
        RootBlock BuildRootBlock(BlockFile block)
        {
            block.Diagnostics = Diagnostics;
            return new RootBlock(BlockRoot, block, rootDirectoryPath: Resolved.RenderRootDirectory,
                mftSource: Options.MftSource);
        }

        /// <summary>
        ///     Writes every field of a published result on the root's runtime and counts its catch-up:
        ///     one more for a lost catch-up, which also marks the block unresumable and makes the
        ///     loss the root's report as the newer fact, and zero for one that held. A held catch-up
        ///     keeps the root's report unless <paramref name="clearsCheckpointLoss" /> says the
        ///     publishing operation replaces what it explained. The caller holds
        ///     <see cref="StateLock" />.
        /// </summary>
        void RecordPendingResultLocked(PendingRootResult published, bool clearsCheckpointLoss)
        {
            _lastResult = published;
            _failure = null;
            _lastMftFailure = null;
            if (published.CatchUpLoss is { } catchUpLoss)
            {
                _consecutiveLostCatchUps++;
                _checkpointLoss = catchUpLoss;
                _unresumableCheckpoint = UnresumableCheckpointReason.LostCatchUp;
                return;
            }

            _consecutiveLostCatchUps = 0;
            _recoveryStopped = false;
            _unresumableCheckpoint = published.CacheOnlyUnresumable
                ? UnresumableCheckpointReason.CacheOnlyAdoption
                : null;
            if (published.CheckpointLoss is { } checkpointLoss)
            {
                _checkpointLoss = checkpointLoss;
            }
            else if (clearsCheckpointLoss)
            {
                _checkpointLoss = null;
            }
        }

        /// <summary>
        ///     The publish step of a scan operation: under the root's write gate, then
        ///     <see cref="StateLock" />, assigns the ordinal, builds the <see cref="RootBlock" /> over
        ///     <paramref name="block" />, commits it with every field of <paramref name="produced" />, and
        ///     publishes the snapshot. A batch on the
        ///     root therefore lands on the old block before this or is dropped after it. Retirement
        ///     and publication share the state-lock section. Returns the count of lost catch-ups and
        ///     the handoff to drain after releasing the write gate, outside unpublished-block cleanup.
        ///     A manual rescan clears any recovery queued during production in that same section;
        ///     an automatic <paramref name="recovery" /> keeps its own ticket and state.
        /// </summary>
        async Task<(int ConsecutiveLostCatchUps, WatchHandoff Handoff)> PublishRescannedBlockAsync(
            BlockFile block, PendingRootResult produced,
            bool clearsCheckpointLoss, RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Hooks.PublishInsideWriteGate?.Invoke(Root);
                var rootBlock = BuildRootBlock(block);
                var bindings = BindViews(rootBlock.Mapped);
                lock (StateLock)
                {
                    ThrowIfClosing(cancellationToken);
                    // Abandonment is decided under this lock from the callers' own state, before any
                    // cancellation callback lands, so a caller cancelled or disposed now never publishes.
                    if (recovery is null && _runningRescan is { } running && running.IsAbandonedByEveryCallerLocked())
                    {
                        throw new OperationCanceledException("Every caller of the rescan cancelled.");
                    }

                    var replaced = Block;

                    if (replaced is not null)
                    {
                        new BlockWriter(block).AdvanceGenerationPast(replaced.Block.Generation);
                    }

                    var handoff = RetireWatchAtCommitLocked();
                    if (recovery is null)
                    {
                        ClearRecoveryLocked();
                    }

                    if (replaced is not null)
                    {
                        RetireBlockLocked(replaced);
                    }

                    PublishBindingsLocked(rootBlock, bindings);
                    RecordPendingResultLocked(produced, clearsCheckpointLoss);
                    NoteWatchStateLocked();
                    return (_consecutiveLostCatchUps, handoff);
                }
            }
            finally
            {
                WriteGate.Release();
                RaiseWatchStateChanged();
            }
        }
    }
}
