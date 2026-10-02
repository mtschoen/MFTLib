namespace MFTLib;

/// <summary>Reports record and byte counts for parsing or transferring a drive's packed block.</summary>
public readonly record struct BlockWriteProgress(
    long RecordsProcessed,
    long BytesProcessed,
    long? TotalRecords,
    long? TotalBytes,
    BrokerScanPhase Phase = BrokerScanPhase.Transferring);
