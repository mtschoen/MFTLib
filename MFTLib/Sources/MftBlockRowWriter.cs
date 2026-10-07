using MFTLib.Index;

namespace MFTLib;

/// <summary>Maps MFT records to sparse block rows without resolving paths or retaining batches.</summary>
internal static class MftBlockRowWriter
{
    /// <summary>Writes each batch and reports transfer progress; the caller stamps and completes the block.</summary>
    public static BlockWriteResult WriteBatches(
        BlockWriter writer,
        IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter,
        IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(batches);
        cancellationToken.ThrowIfCancellationRequested();

        if (filter.Profile is not (BrokerScanProfile.Full or BrokerScanProfile.DirectoryIndex))
        {
            throw new InvalidDataException($"Unknown broker scan profile: {filter.Profile}");
        }

        var keepFileNames = filter.CreateKeepSet();
        var freedRows = filter.IncludeFreed ? new FreedRowTrust(writer.Block) : null;
        long recordsWritten = 0;
        long skippedRecordCount = 0;

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch is MftOmittedRecords omitted)
            {
                skippedRecordCount += omitted.OmittedCount;
            }

            foreach (var record in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!record.InUse && freedRows is null)
                {
                    continue;
                }

                freedRows?.Observe(in record);
                if (!ShouldKeepRecord(record, filter.Profile, keepFileNames))
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
            skippedRecordCount, writer.CompactionNeeded);
    }

    static bool ShouldKeepRecord(
        in MftRecord record,
        BrokerScanProfile profile,
        HashSet<string>? keepFileNames)
    {
        // WriteBatches rejects every other profile before the first batch, so this is the
        // DirectoryIndex rule alone: keep directories, plus any file the caller named.
        return profile == BrokerScanProfile.Full ||
            record.IsDirectory ||
            (keepFileNames != null && keepFileNames.Contains(record.FileName));
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
