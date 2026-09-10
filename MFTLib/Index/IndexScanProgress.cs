namespace MFTLib.Index;

/// <summary>
///     One progress sample from whichever producer is building a drive's block.
///     <see cref="TotalRows" /> is null while the producer does not yet know the total, which
///     an enumeration walk never does. <see cref="CurrentDirectory" /> is null for an MFT scan,
///     which has no notion of a current directory.
/// </summary>
public sealed record IndexScanProgress
{
    public required char DriveLetter { get; init; }

    public required IndexScanPhase Phase { get; init; }

    public required uint RowsWritten { get; init; }

    public uint? TotalRows { get; init; }

    public string? CurrentDirectory { get; init; }
}
