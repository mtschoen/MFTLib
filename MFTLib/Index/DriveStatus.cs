namespace MFTLib.Index;

/// <summary>One drive's current state, read straight off its block header.</summary>
public sealed record DriveStatus
{
    public required char DriveLetter { get; init; }

    public required ProducerKind ProducerKind { get; init; }

    public required DriveState State { get; init; }

    public required uint RowCount { get; init; }

    public required DateTime ScanTimestamp { get; init; }

    public required bool CompactionNeeded { get; init; }

    /// <summary>False on enumeration blocks, which have no journal cursor and no live watch.</summary>
    public required bool WatchSupported { get; init; }

    /// <summary>How many subtrees the producer skipped because it was denied access.</summary>
    public int AccessDeniedSubtreeCount { get; init; }

    /// <summary>
    ///     Set when opening this drive found an existing block at its cache path, rejected it,
    ///     and cold-scanned instead. Null when the current block came from a warm start or a
    ///     first-ever scan with nothing to reject.
    /// </summary>
    public BlockValidationResult? DiscardedBlock { get; init; }
}
