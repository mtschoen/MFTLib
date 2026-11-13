namespace MFTLib;

/// <summary>The client-created section a scan writes into, as its ArmAndScan request names it.</summary>
/// <param name="Name">The section's published name.</param>
/// <param name="DeletesOnClose">The client created the section's file delete-on-close, so completion skips the flush.</param>
readonly record struct BlockSectionRequest(string Name, bool DeletesOnClose);

/// <summary>Writes a scan's rows into a client-created block section and completes its header.</summary>
interface IBlockSectionWriter
{
    /// <summary>
    ///     Writes the source's rows as it parses, stamps the cursor armed before the scan, then completes the block,
    ///     flushing it unless <paramref name="section" /> says the client created its file delete-on-close.
    /// </summary>
    BlockWriteResult Write(
        BlockSectionRequest section,
        UsnJournalCursor cursor,
        IMftSliceSource source,
        MftScanOptions scanOptions,
        BlockWriteReporting reporting,
        CancellationToken cancellationToken);
}
