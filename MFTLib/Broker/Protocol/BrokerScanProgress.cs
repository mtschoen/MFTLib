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
    public long BytesProcessed { get; init; }
    public long? TotalRecords { get; init; }
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
