namespace MFTLib;

/// <summary>Block header totals and skipped input records after writing all batches.</summary>
/// <param name="RowCount">Sparse row extent, including unused slots below the highest written row.</param>
/// <param name="NamePoolUsedBytes">Bytes occupied by names in the block.</param>
/// <param name="SkippedRecordCount">Records without a usable name or a writable row.</param>
/// <param name="CompactionNeeded">Whether the block header requests compaction.</param>
internal readonly record struct BlockWriteResult(
    long RowCount, long NamePoolUsedBytes, long SkippedRecordCount, bool CompactionNeeded);

/// <summary>Where one block write reports what it is doing.</summary>
/// <param name="Progress">Receives per-batch transfer progress for the scan's progress frames; null reports none.</param>
/// <param name="Operation">
///     Receives a processing step for each flushed range of the completed block, so a long flush
///     keeps restarting the scan pipe's progress clock; null reports none.
/// </param>
internal readonly record struct BlockWriteReporting(IProgress<BlockWriteProgress>? Progress, IBrokerOperationReporter? Operation);

/// <summary>Writes record batches into a client-created block section and completes its header.</summary>
internal interface IBlockSectionWriter
{
    /// <summary>Writes all batches, stamps the cursor armed before the scan, then completes the block.</summary>
    BlockWriteResult Write(
        string sectionName,
        UsnJournalCursor cursor,
        IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter,
        BlockWriteReporting reporting,
        CancellationToken cancellationToken);
}
