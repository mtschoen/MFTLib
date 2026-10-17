using MFTLib.Index;

namespace MFTLib;

/// <summary>Maps MFT records to sparse block rows without resolving paths or retaining batches.</summary>
internal static class MftBlockRowWriter
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

        var retainedFileNames = scanOptions.RetainedFileNames is { } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;
        var freedRows = scanOptions.IncludeFreed ? new FreedRowTrust(writer.Block) : null;
        long recordsWritten = 0;
        long skippedRecordCount = 0;

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
                if (!ShouldKeepRecord(record, retainedFileNames))
                {
                    continue;
                }

                if (TryWriteRecord(writer, record))
                {
                    recordsWritten++;
                    freedRows?.Wrote(in record);
                }
                else
                {
                    skippedRecordCount++;
                }
            }

            progress?.Report(new BlockWriteProgress(recordsWritten, writer.Block.Header.NamePoolUsed,
                null, null));
            cancellationToken.ThrowIfCancellationRequested();
        }

        // A freed record's parent can arrive after it, so trust is decided once every row is written.
        freedRows?.DetachUntrusted(writer, cancellationToken);
        return new BlockWriteResult(writer.RowCount, writer.Block.Header.NamePoolUsed,
            skippedRecordCount);
    }

    static bool ShouldKeepRecord(
        in MftRecord record,
        HashSet<string>? retainedFileNames)
    {
        // Null is a full scan; an empty set keeps directories only.
        return retainedFileNames is null ||
            record.IsDirectory ||
            retainedFileNames.Contains(record.FileName);
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
