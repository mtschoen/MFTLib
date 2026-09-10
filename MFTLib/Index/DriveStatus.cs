namespace MFTLib.Index;

/// <summary>
///     One drive's current state. Online status includes block-header data; blockless status
///     carries zero row counts.
/// </summary>
public sealed record DriveStatus
{
    public required char DriveLetter { get; init; }

    public required ProducerKind ProducerKind { get; init; }

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
    ///     Set when this drive's MFT producer failed during opening or its latest rescan. A drive
    ///     that fails during opening has <see cref="DriveState.Failed" /> and no block. A failed
    ///     rescan leaves the previous block in place. Null after a successful MFT production,
    ///     when enumeration was selected explicitly, or on a warm start.
    /// </summary>
    public string? MftProducerFailureMessage { get; init; }

    /// <summary>
    ///     Set when this drive's live watch failed, from the message of the exception that ended
    ///     it. The drive stays <see cref="DriveState.Ready" />: its block is valid and every query
    ///     still answers from it, but nothing after the failing batch has been applied. Cleared
    ///     when the drive is armed again by <see cref="FileIndex.StartWatchingAsync" /> or by a
    ///     rescan. Null while the watch is healthy or not running.
    /// </summary>
    public string? WatchFailureMessage { get; init; }
}
