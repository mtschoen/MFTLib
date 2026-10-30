using MFTLib.Index;

namespace MFTLib;

/// <summary>Maps MFT records to sparse block rows without resolving paths or retaining batches.</summary>
static class MftBlockRowWriter
{
    /// <summary>Writes each batch and reports transfer progress; the caller stamps and completes the block.</summary>
    public static BlockWriteResult WriteBatches(
        BlockWriter writer,
        IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftScanOptions scanOptions,
        IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(batches);
        cancellationToken.ThrowIfCancellationRequested();

        var freedRows = scanOptions.IncludeFreed ? new FreedRowTrust(writer.Block) : null;
        long recordsWritten = 0;
        long skippedRecordCount = 0;

        var stoppedForGrowth = false;
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var record in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!record.InUse && freedRows is null)
                {
                    continue;
                }

                freedRows?.Observe(in record);
                if (TryWriteRecord(writer, record))
                {
                    recordsWritten++;
                    freedRows?.Wrote(in record);
                }
                else
                {
                    skippedRecordCount++;
                    // A producer retries this scan into a larger block, so the remaining batches are not copied into this one and its postings are never built.
                    if (writer.NameRegionsNeedGrowth)
                    {
                        stoppedForGrowth = true;
                        break;
                    }
                }
            }

            if (stoppedForGrowth)
            {
                break;
            }

            progress?.Report(new BlockWriteProgress(recordsWritten, writer.Block.NamePoolUsed,
                null, null));
            cancellationToken.ThrowIfCancellationRequested();
        }

        // A freed record's parent can arrive after it, so trust is decided once every row is written.
        if (!stoppedForGrowth)
        {
            freedRows?.DetachUntrusted(writer, cancellationToken);
        }

        return new BlockWriteResult(writer.RowCount, writer.Block.NamePoolUsed,
            skippedRecordCount);
    }

    static bool TryWriteRecord(BlockWriter writer, MftRecord record)
    {
        // Both identifiers are 48-bit on disk. A truncated parent would point the row at an
        // unrelated record, so an out-of-range record number and an out-of-range parent are
        // both skipped and counted rather than written.
        if (record.RecordNumber > uint.MaxValue || record.ParentRecordNumber > uint.MaxValue)
        {
            return false;
        }

        var name = record.FileName;
        if (name.Length == 0)
        {
            return false;
        }

        // A freed record has the flags a journal delete leaves, so a deleted row has one shape in a block.
        var flags = record.InUse ? RowFlags.InUse : RowFlags.InUse | RowFlags.Tombstone;
        if (record.IsDirectory)
        {
            flags |= RowFlags.Directory;
        }

        if (!record.SizeKnown)
        {
            flags |= RowFlags.SizeUnknown;
        }

        var columns = new RowColumns((uint)record.ParentRecordNumber, flags,
            (uint)record.FileAttributes, record.IsDirectory ? 0 : record.Size, record.ModifiedUtc.Ticks,
            record.SequenceNumber);
        return writer.TryWriteRow((uint)record.RecordNumber, name, in columns);
    }
}
