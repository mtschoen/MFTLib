using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Edits an open block through the production block writer. Valid only inside
///     <see cref="SyntheticBlock.Edit" />: any use after the edit returns throws
///     <see cref="InvalidOperationException" />.
/// </summary>
public sealed class SyntheticBlockEditor
{
    readonly BlockFile _block;
    readonly BlockWriter _writer;
    bool _closed;

    internal SyntheticBlockEditor(BlockFile block)
    {
        _block = block;
        _writer = new BlockWriter(block);
    }

    /// <summary>The highest row written plus one, including tombstones and gaps.</summary>
    internal uint RowCount
    {
        get
        {
            EnsureOpen();
            return _writer.RowCount;
        }
    }

    /// <summary>Reads one stored row.</summary>
    /// <param name="row">The row number; it must be in use.</param>
    /// <returns>The row as stored.</returns>
    internal SyntheticRow ReadRow(uint row)
    {
        RequireRow(row);
        return SyntheticBlock.ReadRow(_block, row);
    }

    /// <summary>Writes a row, appending its name. Fails when the block's capacity is exhausted.</summary>
    /// <param name="row">The row to write; it replaces any row with the same number.</param>
    internal void WriteRow(SyntheticRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        EnsureOpen();
        SyntheticBlock.WriteRow(_writer, row);
    }

    /// <summary>Replaces the NTFS attributes of a row in use.</summary>
    /// <param name="row">The row number.</param>
    /// <param name="attributes">The new attributes.</param>
    public void SetAttributes(uint row, FileAttributes attributes)
    {
        RequireRow(row);
        _block.Rows[(int)row].Attributes = (uint)attributes;
    }

    /// <summary>
    ///     Marks a row's size as unknown, the way an enumeration producer records a file it could not
    ///     stat: the unknown flag is set and the stored size becomes zero. The name is not rewritten.
    /// </summary>
    /// <param name="row">The row number; it must be in use.</param>
    internal void MarkSizeUnknown(uint row)
    {
        RequireRow(row);
        WriteSizeUnknown(row);
    }

    /// <summary>Marks the size of every row in use whose stored value satisfies <paramref name="matches" /> as unknown.</summary>
    /// <param name="matches">Chooses the rows, from each row as stored.</param>
    /// <returns>How many rows were marked, so a test can assert that its filter selected something.</returns>
    public int MarkSizesUnknown(Func<SyntheticRow, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        EnsureOpen();
        var marked = 0;
        for (var row = 0u; row < _writer.RowCount; row++)
        {
            if (_block.Rows[(int)row].IsInUse && matches(SyntheticBlock.ReadRow(_block, row)))
            {
                WriteSizeUnknown(row);
                marked++;
            }
        }

        return marked;
    }

    /// <summary>Marks a row deleted, keeping its name.</summary>
    /// <param name="row">The row number.</param>
    internal void MarkTombstone(uint row)
    {
        RequireRow(row);
        _writer.MarkTombstone(row);
    }

    /// <summary>Marks the block stale, as when an update did not fit, so the next open rescans it.</summary>
    public void MarkCompactionNeeded()
    {
        EnsureOpen();
        _writer.MarkCompactionNeeded();
    }

    /// <summary>Replaces the journal position from which a watch would resume.</summary>
    /// <param name="cursor">The new position.</param>
    internal void SetJournalCursor(SyntheticJournalCursor cursor)
    {
        EnsureOpen();
        _writer.SetJournalCursor(cursor.JournalIdentifier, cursor.NextUsn);
    }

    /// <summary>Replaces the scan timestamp and flushes the block.</summary>
    /// <param name="completedUtc">The new scan timestamp; <see cref="SyntheticDriveHeader.CompletedUtc" /> from <see cref="SyntheticIndexInspection.ReadHeader" /> writes the stored one back.</param>
    public void Complete(DateTime completedUtc)
    {
        EnsureOpen();
        _writer.Complete(completedUtc, null);
    }

    /// <summary>
    ///     Replaces the producer recorded in the header. A block written by <see cref="ProducerKind.Enumeration" />
    ///     becomes one an index treats as an MFT block: it can be warm-started without a producer and its
    ///     rows accept journal entries. Only the header changes, so the rows must already suit the new kind.
    /// </summary>
    /// <param name="producerKind">The producer to record.</param>
    public void SetProducerKind(ProducerKind producerKind)
    {
        EnsureOpen();
        _block.Header.ProducerKind = producerKind;
    }

    /// <summary>
    ///     Replaces the cache identity in place, preserving every other header field and all rows.
    ///     An index opened with a different requested tag rejects the block as a cache-tag mismatch.
    /// </summary>
    /// <param name="cacheTag">The identity to store, including the all-zero default.</param>
    public void SetCacheTag(CacheTag cacheTag)
    {
        EnsureOpen();
        _writer.SetCacheTag(cacheTag);
    }

    /// <summary>
    ///     Zeroes the header's used name-pool length so the block fails name validation. The
    ///     block is no longer consistent, so nothing else should follow it in the same edit.
    /// </summary>
    public void CorruptNamePool()
    {
        EnsureOpen();
        _block.Header.NamePoolUsed = 0;
    }

    /// <summary>
    ///     Sets the size-unknown flag by rewriting the whole descriptor word, as the block writer does
    ///     for every flag change, and zeroes the stored size.
    /// </summary>
    void WriteSizeUnknown(uint rowIndex)
    {
        using var access = _block.TakeAccess();
        ref var stored = ref _block.Rows[(int)rowIndex];
        var descriptor = FileRow.ReadDescriptorWord(in stored);
        FileRow.WriteDescriptorWord(ref stored, FileRow.DescriptorNameOffsetBytes(descriptor),
            FileRow.DescriptorNameLengthUnits(descriptor), FileRow.DescriptorFlags(descriptor) | RowFlags.SizeUnknown);
        stored.Size = 0;
    }

    internal void Close()
    {
        _closed = true;
    }

    void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException(
                "The editor is valid only inside SyntheticBlock.Edit and that edit has returned.");
        }
    }

    void RequireRow(uint row)
    {
        EnsureOpen();
        if (row >= _writer.RowCount || !_block.Rows[(int)row].IsInUse)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "The row is not in use.");
        }
    }
}
