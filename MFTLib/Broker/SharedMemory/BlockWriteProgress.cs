namespace MFTLib;

/// <summary>Reports record and byte counts for parsing or transferring a drive's packed block.</summary>
public readonly record struct BlockWriteProgress(
    long RecordsProcessed,
    long BytesProcessed,
    long? TotalRecords,
    long? TotalBytes,
    BrokerScanPhase Phase = BrokerScanPhase.Transferring)
{
    public BlockWriteProgress(
        long recordsProcessed,
        long bytesProcessed,
        long? totalRecords,
        long? totalBytes)
        : this(recordsProcessed, bytesProcessed, totalRecords, totalBytes, BrokerScanPhase.Transferring)
    {
    }

    public void Deconstruct(
        out long recordsProcessed,
        out long bytesProcessed,
        out long? totalRecords,
        out long? totalBytes)
    {
        recordsProcessed = RecordsProcessed;
        bytesProcessed = BytesProcessed;
        totalRecords = TotalRecords;
        totalBytes = TotalBytes;
    }
}
