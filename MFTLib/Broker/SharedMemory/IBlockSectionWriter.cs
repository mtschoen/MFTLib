namespace MFTLib;

/// <summary>Writes record batches into a client-created block section and completes its header.</summary>
interface IBlockSectionWriter
{
    /// <summary>Writes all batches, stamps the cursor armed before the scan, then completes the block.</summary>
    BlockWriteResult Write(
        string sectionName,
        UsnJournalCursor cursor,
        IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftScanOptions scanOptions,
        BlockWriteReporting reporting,
        CancellationToken cancellationToken);
}
