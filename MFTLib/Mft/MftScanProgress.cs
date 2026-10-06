namespace MFTLib;

/// <summary>
///     Progress sample emitted during an MFT scan.
/// </summary>
internal readonly record struct MftScanProgress(
    MftScanPhase Phase,
    long RecordsScanned,
    long TotalRecords,
    TimeSpan Elapsed);
