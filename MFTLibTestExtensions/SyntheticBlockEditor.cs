using MFTLib;
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
    public uint RowCount
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
    public SyntheticRow ReadRow(uint row)
    {
        RequireRow(row);
        return SyntheticBlock.ReadRow(_block, row);
    }

    /// <summary>Writes a row, appending its name. Fails when the block's capacity is exhausted.</summary>
    /// <param name="row">The row to write; it replaces any row with the same number.</param>
    public void WriteRow(SyntheticRow row)
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

    /// <summary>Marks a row deleted, keeping its name.</summary>
    /// <param name="row">The row number.</param>
    public void MarkTombstone(uint row)
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
    public void SetJournalCursor(UsnJournalCursor cursor)
    {
        EnsureOpen();
        _writer.SetJournalCursor(cursor.JournalId, cursor.NextUsn);
    }

    /// <summary>Replaces the scan timestamp and flushes the block.</summary>
    /// <param name="completedUtc">The new scan timestamp.</param>
    public void Complete(DateTime completedUtc)
    {
        EnsureOpen();
        _writer.Complete(completedUtc, null);
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
