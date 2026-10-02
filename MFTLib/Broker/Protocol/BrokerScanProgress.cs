namespace MFTLib;

/// <summary>
///     Progress sample emitted during an elevated broker drive scan.
/// </summary>
public readonly record struct BrokerScanProgress
{
    public required string DriveLetter { get; init; }
    public BrokerScanPhase Phase { get; init; }
    public long RecordsProcessed { get; init; }

    /// <summary>
    ///     Name pool bytes written so far, not the block's total byte length. A consumer
    ///     rendering a byte figure is seeing one region of the block, not all of it.
    /// </summary>
    public long BytesProcessed { get; init; }

    public long? TotalRecords { get; init; }

    /// <summary>
    ///     Name pool bytes expected in total, on the same measure as
    ///     <see cref="BytesProcessed" />. The block scan reports the two as equal on its
    ///     final sample, so it conveys completion rather than a ratio.
    /// </summary>
    public long? TotalBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
}
