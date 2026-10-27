namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     The block's ownership passes to the <see cref="DriveBlock" />, which releases it through
        ///     <see cref="DriveBlock.ReleaseState" /> (see its own summary), not through
        ///     <see cref="IDisposable" />.
        /// </summary>
        DriveBlock BuildDriveBlock(BlockFile block) =>
            new(Drive, block, rootDirectoryPath: Configuration.RootDirectory,
                mftSource: Options.MftSource);

        /// <summary>
        ///     Writes every field of a published result on the drive's runtime and counts its catch-up:
        ///     one more for a lost catch-up, which also marks the block unresumable and makes the
        ///     loss the drive's report as the newer fact, and zero for one that held. A held catch-up
        ///     keeps the drive's report unless <paramref name="clearsCheckpointLoss" /> says the
        ///     publishing operation replaces what it explained. The caller holds
        ///     <see cref="_stateLock" />.
        /// </summary>
        void RecordPendingResultLocked(PendingDriveResult published, bool clearsCheckpointLoss)
        {
            _lastResult = published;
            _failure = null;
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
        ///     The publish step of a scan operation: under the drive's write gate, then
        ///     <see cref="_stateLock" />, assigns the ordinal, builds the <see cref="DriveBlock" /> over
        ///     <paramref name="block" />, commits it with every field of <paramref name="produced" />, and
        ///     publishes the snapshot. A batch on the
        ///     drive therefore lands on the old block before this or is dropped after it. Retirement
        ///     and publication share the state-lock section. Returns the count of lost catch-ups and
        ///     the handoff to drain after releasing the write gate, outside unpublished-block cleanup.
        ///     A manual rescan clears any recovery queued during production in that same section;
        ///     an automatic <paramref name="recovery" /> keeps its own ticket and state.
        /// </summary>
        async Task<(int ConsecutiveLostCatchUps, WatchHandoff Handoff)> PublishRescannedBlockAsync(
            BlockFile block, PendingDriveResult produced,
            bool clearsCheckpointLoss, RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Hooks.PublishInsideWriteGate?.Invoke(Drive);
                lock (StateLock)
                {
                    Index.ThrowIfCancelledByDisposal(cancellationToken);
                    var replaced = Block;

                    if (replaced is not null)
                    {
                        new BlockWriter(block).AdvanceGenerationPast(replaced.Block.Generation);
                    }

                    var driveBlock = BuildDriveBlock(block);
                    var handoff = RetireWatchAtCommitLocked();
                    if (recovery is null)
                    {
                        ClearRecoveryLocked();
                    }

                    if (replaced is not null)
                    {
                        RetireBlockLocked(replaced);
                    }

                    Block = driveBlock;
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
