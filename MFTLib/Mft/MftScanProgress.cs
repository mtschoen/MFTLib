namespace MFTLib;

/// <summary>
///     Progress sample emitted during an MFT scan.
/// </summary>
readonly record struct MftScanProgress(
    long RecordsScanned,
    long TotalRecords,
    TimeSpan Elapsed);
