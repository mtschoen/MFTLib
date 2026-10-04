namespace MFTLib.Index;

/// <summary>
///     One progress sample from whichever producer is building a drive's block.
///     <see cref="TotalRows" /> is null while the producer does not yet know the total, which
///     an enumeration walk never does. <see cref="CurrentDirectory" /> is null for an MFT scan,
///     which has no notion of a current directory. The <see cref="IndexScanPhase.Finished" />
///     sample ends a drive's scan: <see cref="Outcome" /> is non-null only on it, and
///     <see cref="RowsWritten" /> is then the finished block's row count on success and 0 otherwise.
/// </summary>
public sealed record IndexScanProgress
{
    /// <summary>Drive whose block is being produced.</summary>
    public required char DriveLetter { get; init; }

    /// <summary>Current stage of the scan.</summary>
    public required IndexScanPhase Phase { get; init; }

    /// <summary>Number of rows written so far, or final row count on a successful completion sample.</summary>
    public required uint RowsWritten { get; init; }

    /// <summary>Expected total rows when known; otherwise, null.</summary>
    public uint? TotalRows { get; init; }

    /// <summary>Current directory during enumeration, or null for producers without directories.</summary>
    public string? CurrentDirectory { get; init; }

    /// <summary>Terminal scan outcome on a finished sample; otherwise, null.</summary>
    public IndexScanOutcome? Outcome { get; init; }
}
