namespace MFTLib;

enum BlockScanPhase : byte
{
    Parsing = 0,
    Transferring = 1
}

/// <summary>Reports record and byte counts for parsing or transferring a volume's packed block.</summary>
readonly record struct BlockWriteProgress(
    long RecordsProcessed,
    long BytesProcessed,
    long? TotalRecords,
    long? TotalBytes,
    BlockScanPhase Phase = BlockScanPhase.Transferring);
