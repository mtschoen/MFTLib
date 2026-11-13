namespace MFTLib;

/// <summary>Writes a scan's rows into a client-created block section and completes its header.</summary>
interface IBlockSectionWriter
{
    /// <summary>Writes the source's rows as it parses, stamps the cursor armed before the scan, then completes the block.</summary>
    BlockWriteResult Write(
        string sectionName,
        UsnJournalCursor cursor,
        IMftSliceSource source,
        MftScanOptions scanOptions,
        BlockWriteReporting reporting,
        CancellationToken cancellationToken);
}
