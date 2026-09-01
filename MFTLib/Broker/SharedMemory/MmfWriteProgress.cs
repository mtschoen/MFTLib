namespace MFTLib;

public readonly record struct MmfWriteProgress(
    long RecordsProcessed,
    long BytesProcessed,
    long? TotalRecords,
    long? TotalBytes,
    BrokerScanPhase Phase = BrokerScanPhase.Transferring)
{
    public MmfWriteProgress(
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
