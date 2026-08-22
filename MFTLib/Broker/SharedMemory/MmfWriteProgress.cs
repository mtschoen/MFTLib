namespace MFTLib;

public readonly record struct MmfWriteProgress(
    long RecordsProcessed,
    long BytesProcessed,
    long? TotalRecords,
    long? TotalBytes);
