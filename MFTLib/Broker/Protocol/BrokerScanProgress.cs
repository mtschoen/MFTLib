namespace MFTLib;

public readonly record struct BrokerScanProgress(
    string DriveLetter,
    long RecordsProcessed,
    long BytesProcessed,
    long? TotalRecords,
    long? TotalBytes,
    TimeSpan Elapsed);
