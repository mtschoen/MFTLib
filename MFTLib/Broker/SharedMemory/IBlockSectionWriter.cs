namespace MFTLib;

/// <summary>Block header totals and skipped input records after writing all batches.</summary>
/// <param name="RowCount">Sparse row extent, including unused slots below the highest written row.</param>
/// <param name="NamePoolUsedBytes">Bytes occupied by names in the block.</param>
/// <param name="SkippedRecordCount">Records without a usable name or a writable row.</param>
/// <param name="CompactionNeeded">Whether the block header requests compaction.</param>
public readonly record struct BlockWriteResult(
    long RowCount, long NamePoolUsedBytes, long SkippedRecordCount, bool CompactionNeeded);

/// <summary>Writes record batches into a client-created block section and completes its header.</summary>
public interface IBlockSectionWriter
{
    /// <summary>Writes all batches, stamps the cursor armed before the scan, then completes the block.</summary>
    BlockWriteResult Write(
        string sectionName,
        UsnJournalCursor cursor,
        IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter,
        IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken);
}
