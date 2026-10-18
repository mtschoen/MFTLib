namespace MFTLib;

/// <summary>Writes record batches into a client-created block section and completes its header.</summary>
internal interface IBlockSectionWriter
{
    /// <summary>Writes all batches, stamps the cursor armed before the scan, then completes the block.</summary>
    BlockWriteResult Write(
        string sectionName,
        UsnJournalCursor cursor,
        IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftRowSelection selection,
        BlockWriteReporting reporting,
        CancellationToken cancellationToken);
}
