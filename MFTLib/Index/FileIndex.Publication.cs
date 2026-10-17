namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Everything one settle or scan attempt of a drive learned, held locally until the publish
    ///     step records it. The publish step assigns the drive's ordinal (the next free one for a drive
    ///     with no block, its existing one otherwise), builds the <see cref="DriveBlock" /> over
    ///     <see cref="Block" />, and writes the result on the drive's runtime in one step under
    ///     <see cref="_stateLock" />. A result with no block is recorded by drive instead.
    /// </summary>
    internal sealed record PendingDriveResult
    {
        /// <summary>
        ///     The produced or warm-started block, which this result owns until it is published and
        ///     whose mapping is closed directly if it never is. Null when there is none.
        /// </summary>
        public BlockFile? Block { get; init; }

        public BlockOrigin Origin { get; init; }

        public int AccessDeniedSubtreeCount { get; init; }

        public int SkippedRecordCount { get; init; }

        /// <summary>The MFT producer's failure when it produced no block.</summary>
        public Exception? ProducerFailure { get; init; }

        /// <summary>Why the drive has no block: the producer's failure, or the cache-only refusal.</summary>
        public string? ProducerFailureMessage { get; init; }

        /// <summary>Rejects a blockless result without failure detail instead of publishing a contradictory drive status.</summary>
        /// <param name="drive">The drive, named in the exception when the message is missing.</param>
        /// <returns>The message every blockless result carries.</returns>
        /// <exception cref="InvalidOperationException">The result has no failure message.</exception>
        public string RequireFailureMessage(DriveKey drive) =>
            ProducerFailureMessage ?? throw new InvalidOperationException(
                $"Drive {drive} failed without a failure message.");

        /// <summary>The loss that made the open reject or distrust the drive's cached block.</summary>
        public JournalCheckpointLoss? CheckpointLoss { get; init; }

        public CacheLocation? CacheLocation { get; init; }

        /// <summary>A cache-only open adopted the block despite <see cref="CheckpointLoss" />.</summary>
        public bool CacheOnlyUnresumable { get; init; }

        /// <summary>The scan's lost catch-up, as the producer's journal proved it.</summary>
        public JournalCheckpointLoss? CatchUpLoss { get; init; }
    }

    /// <summary>
    ///     The block's ownership passes to the <see cref="DriveBlock" />, which releases it through
    ///     <see cref="DriveBlock.ReleaseState" /> (see its own summary), not through
    ///     <see cref="IDisposable" />.
    /// </summary>
    DriveBlock BuildDriveBlock(IndexedDrive drive, ushort driveOrdinal, BlockFile block) =>
        new(drive.Drive, driveOrdinal, block, rootDirectoryPath: drive.RootDirectory,
            mftSource: _options.MftSource);

    /// <summary>
    ///     Writes every field of a published result on the drive's runtime and counts its catch-up:
    ///     one more for a lost catch-up, which also marks the block unresumable and makes the
    ///     loss the drive's report as the newer fact, and zero for one that held. A held catch-up
    ///     keeps the drive's report unless <paramref name="clearsCheckpointLoss" /> says the
    ///     publishing operation replaces what it explained. The caller holds
    ///     <see cref="_stateLock" />.
    /// </summary>
    static void RecordPendingResultLocked(DriveRuntime runtime, PendingDriveResult published,
        bool clearsCheckpointLoss)
    {
        runtime.LastResult = published;
        runtime.Failure = null;
        if (published.CatchUpLoss is { } catchUpLoss)
        {
            runtime.ConsecutiveLostCatchUps++;
            runtime.CheckpointLoss = catchUpLoss;
            runtime.UnresumableCheckpoint = UnresumableCheckpointReason.LostCatchUp;
            return;
        }

        runtime.ConsecutiveLostCatchUps = 0;
        runtime.RecoveryStopped = false;
        runtime.UnresumableCheckpoint = published.CacheOnlyUnresumable
            ? UnresumableCheckpointReason.CacheOnlyAdoption
            : null;
        if (published.CheckpointLoss is { } checkpointLoss)
        {
            runtime.CheckpointLoss = checkpointLoss;
        }
        else if (clearsCheckpointLoss)
        {
            runtime.CheckpointLoss = null;
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
        DriveRuntime runtime, BlockFile block, PendingDriveResult produced,
        bool clearsCheckpointLoss, RecoveryTicket? recovery, CancellationToken cancellationToken)
    {
        await runtime.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Hooks.PublishInsideWriteGate?.Invoke(runtime.Drive);
            lock (_stateLock)
            {
                ThrowIfCancelledByDisposal(cancellationToken);
                var replaced = runtime.Block;
                var driveOrdinal = replaced?.DriveOrdinal ?? AllocateDriveOrdinalLocked();

                var driveBlock = BuildDriveBlock(runtime.Configuration, driveOrdinal, block);
                var handoff = RetireWatchAtCommitLocked(runtime);
                if (recovery is null)
                {
                    ClearRecoveryLocked(runtime);
                }

                if (replaced is not null)
                {
                    RetireBlockLocked(replaced);
                }

                runtime.Block = driveBlock;
                RecordPendingResultLocked(runtime, produced, clearsCheckpointLoss);
                PublishSnapshotLocked();
                NoteWatchStateLocked(runtime);
                return (runtime.ConsecutiveLostCatchUps, handoff);
            }
        }
        finally
        {
            runtime.WriteGate.Release();
            RaiseWatchStateChanged(runtime);
        }
    }
}
