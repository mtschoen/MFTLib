using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The one implementation that turns MFT record batches into a completed index block. The broker host
///     and every direct producer call it; none of them carry a pipe, a frame writer or a journal query into it.
/// </summary>
static class MftBlockScan
{
    // The processing step each flushed range of the completed block publishes.
    internal const string FlushStep = "block flush";

    // The processing step the posting build publishes, so a host sees progress while the base is written.
    internal const string PostingBuildStep = "posting build";

    /// <summary>Fits a scan's skipped count into the public diagnostic without failing a completed scan.</summary>
    internal static int SaturateSkippedRecordCount(long skippedRecordCount) =>
        (int)Math.Min(skippedRecordCount, int.MaxValue);

    /// <summary>Writes all batches under the scan options, stamps the journal cursor, then completes the block.</summary>
    /// <param name="block">The block section to fill.</param>
    /// <param name="stamp">The journal cursor and completion clock to stamp into the block; a dump or uncached local producer passes a zero cursor.</param>
    /// <param name="batches">The record batches to write.</param>
    /// <param name="scanOptions">Freed-record inclusion for the scan.</param>
    /// <param name="reporting">Where the write publishes its progress.</param>
    /// <param name="cancellationToken">Cancels the write between records and between batches.</param>
    /// <returns>The row count, name pool usage and skipped record count of the completed block.</returns>
    internal static BlockWriteResult WriteToBlock(
        BlockFile block, BlockStamp stamp, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftScanOptions scanOptions, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        var writer = new BlockWriter(block);
        var result = MftBlockRowWriter.WriteBatches(writer, batches, scanOptions, reporting.Progress,
            cancellationToken);
        writer.SetJournalCursor(stamp.Cursor.JournalIdentifier, stamp.Cursor.NextUsn);
        var operation = reporting.Operation;
        writer.Complete(stamp.TimeProvider.GetUtcNow().UtcDateTime,
            operation is null ? null : _ => operation.Processing(FlushStep),
            operation is null ? null : () => operation.Processing(PostingBuildStep),
            // A producer re-runs a scan whose name regions ran out, so the posting build would be discarded with it.
            !writer.NameRegionsNeedGrowth, cancellationToken);
        return result;
    }
}

/// <summary>What a completed block records about when and where in the journal it was written.</summary>
/// <param name="Cursor">The journal cursor armed before the scan; zero for a dump or uncached local scan.</param>
/// <param name="TimeProvider">Read once, after the last batch is written, for the completion time stamped into the block header.</param>
readonly record struct BlockStamp(UsnJournalCursor Cursor, TimeProvider TimeProvider);
