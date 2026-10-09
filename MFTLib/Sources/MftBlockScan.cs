using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The one implementation that turns MFT record batches into a completed index block. The broker host
///     and every direct producer call it; none of them carry a pipe, a frame writer or a journal query into it.
/// </summary>
internal static class MftBlockScan
{
    // The processing step each flushed range of the completed block publishes.
    internal const string FlushStep = "block flush";

    /// <summary>Writes all batches through the filter, stamps the journal cursor, then completes the block.</summary>
    /// <param name="block">The block section to fill.</param>
    /// <param name="stamp">The journal cursor and completion clock to stamp into the block; a dump or uncached local producer passes a zero cursor.</param>
    /// <param name="batches">The record batches to write.</param>
    /// <param name="filter">Selects which records receive block rows.</param>
    /// <param name="reporting">Where the write publishes its progress.</param>
    /// <param name="cancellationToken">Cancels the write between records and between batches.</param>
    /// <returns>The row count, name pool usage and skipped record count of the completed block.</returns>
    internal static BlockWriteResult WriteToBlock(
        BlockFile block, BlockStamp stamp, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        var writer = new BlockWriter(block);
        var result = MftBlockRowWriter.WriteBatches(writer, batches, filter, reporting.Progress, cancellationToken);
        writer.SetJournalCursor(stamp.Cursor.JournalIdentifier, stamp.Cursor.NextUsn);
        var operation = reporting.Operation;
        writer.Complete(stamp.Clock(), operation is null ? null : _ => operation.Processing(FlushStep));
        return result;
    }
}

/// <summary>What a completed block records about when and where in the journal it was written.</summary>
/// <param name="Cursor">The journal cursor armed before the scan; zero for a dump or uncached local scan.</param>
/// <param name="Clock">Read once, after the last batch is written, for the completion time stamped into the block header.</param>
internal readonly record struct BlockStamp(UsnJournalCursor Cursor, Func<DateTime> Clock);
