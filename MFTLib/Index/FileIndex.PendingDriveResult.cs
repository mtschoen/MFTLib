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
}
