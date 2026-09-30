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
    public required char DriveLetter { get; init; }

    public required IndexScanPhase Phase { get; init; }

    public required uint RowsWritten { get; init; }

    public uint? TotalRows { get; init; }

    public string? CurrentDirectory { get; init; }

    public IndexScanOutcome? Outcome { get; init; }
}
