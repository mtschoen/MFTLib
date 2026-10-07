namespace MFTLib;

/// <summary>
///     Progress sample emitted during an MFT scan.
/// </summary>
internal readonly record struct MftScanProgress(
    long RecordsScanned,
    long TotalRecords,
    TimeSpan Elapsed);
