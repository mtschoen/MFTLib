namespace MFTLib;

public readonly record struct MftScanProgress(
    long RecordsScanned,
    long TotalRecords,
    TimeSpan Elapsed);
