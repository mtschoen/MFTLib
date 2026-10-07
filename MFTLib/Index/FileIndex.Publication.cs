namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Everything one settle or scan attempt of a drive learned, held locally until the publish
    ///     step records it. Nothing unpublished is keyed by an ordinal: the publish step assigns the
    ///     drive's ordinal (the next free one for a drive with no block, its existing one otherwise),
    ///     builds the <see cref="DriveBlock" /> over <see cref="Block" />, and writes every field
    ///     against that ordinal in one step under <see cref="_stateLock" />. A result with no block
    ///     is recorded by drive letter instead.
    /// </summary>
    sealed record PendingDriveResult
    {
        /// <summary>
        ///     The produced or warm-started block, which this result owns until it is published and
        ///     whose mapping is closed directly if it never is. Null when there is none.
        /// </summary>
        public BlockFile? Block { get; init; }

        public BlockSource BlockSource { get; init; }

        public int AccessDeniedSubtreeCount { get; init; }

        public int SkippedRecordCount { get; init; }

        /// <summary>The MFT producer's failure when it produced no block.</summary>
        public Exception? ProducerFailure { get; init; }

        /// <summary>Why the drive has no block: the producer's failure, or the cache-only refusal.</summary>
        public string? ProducerFailureMessage { get; init; }

        /// <summary>The loss that made the open reject or distrust the drive's cached block.</summary>
        public JournalCheckpointLoss? CheckpointLoss { get; init; }

        public CacheSlotState CacheSlot { get; init; }

        /// <summary>A cache-only open adopted the block despite <see cref="CheckpointLoss" />.</summary>
        public bool CacheOnlyUnresumable { get; init; }

        /// <summary>The scan's lost catch-up, as the producer's journal proved it.</summary>
        public JournalCheckpointLoss? CatchUpLoss { get; init; }
    }

    /// <summary>
    ///     The block's ownership passes to the <see cref="DriveBlock" />, which releases it through
    ///     the reference-counted <see cref="DriveBlock.Release" /> (see its own summary), not through
    ///     <see cref="IDisposable" />.
    /// </summary>
    DriveBlock BuildDriveBlock(IndexedDrive drive, ushort driveOrdinal, BlockFile block) =>
        new(drive.DriveLetter, driveOrdinal, block, rootDirectoryPath: drive.RootDirectory,
            isMftDump: IsMftDumpDrive(drive.DriveLetter));

    /// <summary>
    ///     Writes every field of a published result against its ordinal and counts its catch-up:
    ///     one more for a lost catch-up, which also marks the block unresumable and makes the
    ///     loss the drive's report as the newer fact, and zero for one that held. A held catch-up
    ///     keeps the drive's report unless <paramref name="clearsCheckpointLoss" /> says the
    ///     publishing operation replaces what it explained. The caller holds
    ///     <see cref="_stateLock" />.
    /// </summary>
    void RecordPendingResultLocked(DriveRuntime runtime, ushort driveOrdinal, PendingDriveResult published,
        bool clearsCheckpointLoss)
    {
        _blockSourcesByOrdinal[driveOrdinal] = published.BlockSource;
        _cacheSlotsByOrdinal[driveOrdinal] = published.CacheSlot;
        _accessDeniedSubtreeCountByOrdinal[driveOrdinal] = published.AccessDeniedSubtreeCount;
        _skippedRecordCountByOrdinal[driveOrdinal] = published.SkippedRecordCount;
        _mftProducerFailureMessagesByOrdinal.Remove(driveOrdinal);
        if (published.CatchUpLoss is { } catchUpLoss)
        {
            runtime.ConsecutiveLostCatchUps++;
            _checkpointLossesByOrdinal[driveOrdinal] = catchUpLoss;
            _unresumableCheckpointsByOrdinal[driveOrdinal] = UnresumableCheckpointReason.LostCatchUp;
            return;
        }

        runtime.ConsecutiveLostCatchUps = 0;
        if (published.CacheOnlyUnresumable)
        {
            _unresumableCheckpointsByOrdinal[driveOrdinal] = UnresumableCheckpointReason.CacheOnlyAdoption;
        }
        else
        {
            _unresumableCheckpointsByOrdinal.Remove(driveOrdinal);
        }

        if (published.CheckpointLoss is { } checkpointLoss)
        {
            _checkpointLossesByOrdinal[driveOrdinal] = checkpointLoss;
        }
        else if (clearsCheckpointLoss)
        {
            _checkpointLossesByOrdinal.Remove(driveOrdinal);
        }
    }

    /// <summary>
    ///     The publish step of a scan operation: under the drive's write gate, then
    ///     <see cref="_stateLock" />, assigns the ordinal, builds the <see cref="DriveBlock" /> over
    ///     <paramref name="block" />, commits it with every field of <paramref name="produced" />, and
    ///     publishes the snapshot. The new snapshot is created before anything is committed, so a
    ///     failure leaves every record as it was and the caller closes the block. A batch on the
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
            PublishInsideWriteGateForTest?.Invoke(runtime.DriveLetter);
            lock (_stateLock)
            {
                ThrowIfCancelledByDisposal(cancellationToken);
                var replacesBlock = TryGetDriveOrdinalLocked(runtime.DriveLetter, out var driveOrdinal);
                if (!replacesBlock)
                {
                    driveOrdinal = (ushort)_driveBlocks.Count;
                }

                var driveBlock = BuildDriveBlock(_driveConfigurations[runtime.DriveLetter], driveOrdinal, block);
                var blocks = new List<DriveBlock>(_driveBlocks);
                if (replacesBlock)
                {
                    blocks[driveOrdinal] = driveBlock;
                }
                else
                {
                    blocks.Add(driveBlock);
                }

                var snapshot = Snapshot.Create(blocks);
                var handoff = RetireWatchAtCommitLocked(runtime);
                if (recovery is null)
                {
                    ClearRecoveryLocked(runtime);
                }

                if (replacesBlock)
                {
                    _driveBlocks[driveOrdinal] = driveBlock;
                }
                else
                {
                    _driveBlocks.Add(driveBlock);
                    _blocklessDriveStatuses.RemoveAll(status =>
                        char.ToUpperInvariant(status.DriveLetter) == runtime.DriveLetter);
                }

                RecordPendingResultLocked(runtime, driveOrdinal, produced, clearsCheckpointLoss);
                RetireCurrentSnapshotLocked(snapshot);
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
