namespace MFTLib.Index;

/// <summary>
///     One drive's current state. Online status includes block-header data; blockless status
///     carries zero row counts.
/// </summary>
public sealed record DriveStatus
{
    public required char DriveLetter { get; init; }

    public required ProducerKind ProducerKind { get; init; }

    /// <summary>
    ///     Where the block behind this status came from. <see cref="BlockSource.None" /> for a
    ///     drive with no block. Reads <see cref="BlockSource.ProducedByScan" /> after a successful
    ///     rescan, because that rescan is what produced the current block.
    /// </summary>
    public required BlockSource BlockSource { get; init; }

    public required DriveState State { get; init; }

    public required uint RowCount { get; init; }

    /// <summary>
    ///     Rows that are in use and not tombstoned. <see cref="RowCount" /> is the highest used
    ///     slot plus one and can include free slots and deleted files.
    /// </summary>
    public required uint LiveRowCount { get; init; }

    public required DateTime ScanTimestamp { get; init; }

    public required bool CompactionNeeded { get; init; }

    /// <summary>True only for an MFT-backed drive with a block and a live-watch cursor.</summary>
    public required bool WatchSupported { get; init; }

    /// <summary>How many subtrees the producer skipped because it was denied access.</summary>
    public int AccessDeniedSubtreeCount { get; init; }

    /// <summary>
    ///     Set when opening this drive found an existing block at its cache path, rejected it,
    ///     and cold-scanned instead. Null when the current block came from a warm start or a
    ///     first-ever scan with nothing to reject.
    /// </summary>
    public BlockValidationResult? DiscardedBlock { get; init; }

    /// <summary>
    ///     The detail behind <see cref="FailureKind" />: the MFT producer's error when the
    ///     producer failed during opening or its latest rescan, or the cache-only refusal when
    ///     <see cref="FileIndexOptions.InitialOpenCacheOnly" /> declined the drive. A drive that
    ///     fails during opening has <see cref="DriveState.Failed" /> and no block. A failed
    ///     rescan leaves the previous block in place. Null after a successful MFT production,
    ///     when enumeration was selected explicitly, or on a warm start.
    /// </summary>
    public string? MftProducerFailureMessage { get; init; }

    /// <summary>
    ///     Why this drive is <see cref="DriveState.Failed" />:
    ///     <see cref="DriveFailureKind.CacheDeclined" /> when a cache-only open declined it for
    ///     lack of a usable cache block, <see cref="DriveFailureKind.InUse" /> when the cache
    ///     block is locked by another live index, <see cref="DriveFailureKind.ProducerFailed" />
    ///     when its MFT producer failed. <see cref="DriveFailureKind.None" /> in every other
    ///     state, including <see cref="DriveState.Offline" />. A successful
    ///     <see cref="FileIndex.RescanAsync" /> of a failed drive clears this back to
    ///     <see cref="DriveFailureKind.None" /> along with the state; a failed rescan of a
    ///     cache-declined drive moves it to <see cref="DriveFailureKind.ProducerFailed" />,
    ///     because the scan itself is now what failed.
    /// </summary>
    public DriveFailureKind FailureKind { get; init; }

    /// <summary>
    ///     Set when this drive's live watch failed, from the message of the exception that ended
    ///     it. The drive stays <see cref="DriveState.Ready" />: its block is valid and every query
    ///     still answers from it, but nothing after the failing batch has been applied. Cleared
    ///     when the drive is armed again by <see cref="FileIndex.StartWatchingAsync" /> or by a
    ///     rescan. Null while the watch is healthy or not running.
    /// </summary>
    public string? WatchFailureMessage { get; init; }

    /// <summary>
    ///     Where this drive's live watch stands in draining the journal backlog that was present
    ///     when its current arm started. Session-scoped: <see cref="WatchCatchUpState.NotStarted" />
    ///     whenever no session is draining the drive (before the first
    ///     <see cref="FileIndex.StartWatchingAsync" /> and again after
    ///     <see cref="FileIndex.StopWatchingAsync" />), <see cref="WatchCatchUpState.CatchingUp" />
    ///     while an arm applies its backlog, <see cref="WatchCatchUpState.CaughtUp" /> once that
    ///     backlog has been applied and the drive is on live entries, and
    ///     <see cref="WatchCatchUpState.Faulted" /> when the drive's watch fails, with detail in
    ///     <see cref="WatchFailureMessage" />. <see cref="FileIndex.RescanAsync" /> resets the one
    ///     drive it re-arms to <see cref="WatchCatchUpState.CatchingUp" />. Always
    ///     <see cref="WatchCatchUpState.NotStarted" /> for a drive that cannot be watched.
    /// </summary>
    public WatchCatchUpState WatchCatchUp { get; init; }

    /// <summary>
    ///     Set when this drive had a cached block that could not be resumed because its
    ///     journal checkpoint was gone, so the drive was cold-scanned instead. It carries the
    ///     checkpoint, the journal as it stood at that moment, and, when a larger journal
    ///     would have prevented the rescan, how large it would have had to be. Null when the
    ///     drive warm-started, had no cache to resume, or was rejected for a reason unrelated
    ///     to the journal, which <see cref="DiscardedBlock" /> covers.
    /// </summary>
    public JournalCheckpointLoss? CheckpointLoss { get; init; }
}
