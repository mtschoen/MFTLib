using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    ///     Initializes a new instance of <see cref="BrokerScanProgress" /> with <see cref="Phase" /> set to <see cref="BrokerScanPhase.Parsing" />.
    /// </summary>
    [SetsRequiredMembers]
    public BrokerScanProgress(
        string driveLetter,
        long recordsProcessed,
        long bytesProcessed,
        long? totalRecords,
        long? totalBytes,
        TimeSpan elapsed)
    {
        DriveLetter = driveLetter;
        Phase = BrokerScanPhase.Parsing;
        RecordsProcessed = recordsProcessed;
        BytesProcessed = bytesProcessed;
        TotalRecords = totalRecords;
        TotalBytes = totalBytes;
        Elapsed = elapsed;
    }

    public void Deconstruct(
        out string driveLetter,
        out long recordsProcessed,
        out long bytesProcessed,
        out long? totalRecords,
        out long? totalBytes,
        out TimeSpan elapsed)
    {
        driveLetter = DriveLetter;
        recordsProcessed = RecordsProcessed;
        bytesProcessed = BytesProcessed;
        totalRecords = TotalRecords;
        totalBytes = TotalBytes;
        elapsed = Elapsed;
    }
}
