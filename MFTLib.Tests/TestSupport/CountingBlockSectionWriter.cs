namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Consumes every slice and counts rows without writing a block, for scans whose block content
///     is not under test or that run on several roots at once (a recording writer owns one block).
/// </summary>
sealed class CountingBlockSectionWriter : IBlockSectionWriter
{
    public BlockWriteResult Write(BlockSectionRequest section, UsnJournalCursor cursor,
        IMftSliceSource source, MftScanOptions scanOptions,
        BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (var slice in source.Slices())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows += slice.EntryCount;
            slice.Return();
        }

        return new BlockWriteResult(rows, 0, source.Finish().RecordsOmitted);
    }
}
