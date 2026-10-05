namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Consumes every batch and counts rows without writing a block, for scans whose block content
///     is not under test or that run on several drives at once (a recording writer owns one block).
/// </summary>
internal sealed class CountingBlockSectionWriter : IBlockSectionWriter
{
    public BlockWriteResult Write(string sectionName, UsnJournalCursor cursor,
        IEnumerable<IReadOnlyList<MftRecord>> batches, MftBlockRowFilter filter,
        BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows += batch.Count;
        }

        return new BlockWriteResult(rows, 0, 0, false);
    }
}
