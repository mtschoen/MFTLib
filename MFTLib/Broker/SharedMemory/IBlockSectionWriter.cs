namespace MFTLib;

/// <summary>Writes a scan's rows into a client-created block section and completes its header.</summary>
interface IBlockSectionWriter
{
    /// <summary>Writes the source's rows as it parses, stamps the cursor armed before the scan, then completes the block. A delete-on-close block is neither flushed nor read back from disk.</summary>
    BlockWriteResult Write(
        BlockSectionTarget target,
        UsnJournalCursor cursor,
        IMftSliceSource source,
        MftScanOptions scanOptions,
        BlockWriteReporting reporting,
        CancellationToken cancellationToken);
}

/// <summary>The client-created section a scan writes into and whether its block is delete-on-close, so never flushed.</summary>
readonly record struct BlockSectionTarget(string SectionName, bool DeleteOnClose);
