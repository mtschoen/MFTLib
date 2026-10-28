namespace MFTLib.Index;

/// <summary>
///     A managed copy of an unfinished block's used rows, sequence numbers, name entries and bytes.
///     Restoring preserves identifiers and rebuilds chains for the replacement bucket count, so an
///     enumeration walk can grow its block without restarting the walk.
/// </summary>
sealed class BlockRowSnapshot
{
    readonly FileRow[] _rows;
    readonly ushort[] _sequenceNumbers;
    const int PoolChunkBytes = 16 * 1024 * 1024;
    readonly byte[][] _names;
    readonly NameEntry[] _nameEntries;
    readonly uint _liveRowCount;

    BlockRowSnapshot(FileRow[] rows, ushort[] sequenceNumbers, byte[][] names, NameEntry[] nameEntries,
        uint namePoolUsed, uint liveRowCount)
    {
        _rows = rows;
        _sequenceNumbers = sequenceNumbers;
        _names = names;
        _nameEntries = nameEntries;
        NamePoolUsed = namePoolUsed;
        _liveRowCount = liveRowCount;
    }

    /// <summary>Rows the snapshot holds; the target block needs at least this slot capacity.</summary>
    public uint RowCount => (uint)_rows.Length;

    /// <summary>Name-pool bytes the snapshot holds; the target block needs at least this name-pool capacity.</summary>
    uint NamePoolUsed { get; }

    /// <summary>Copies the used regions and counts of <paramref name="block" />.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="block" /> is null.</exception>
    public static BlockRowSnapshot Capture(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        using var access = block.TakeAccess();
        var rowCount = (int)block.RowCount;
        var chunks = new List<byte[]>();
        for (uint offset = 0; offset < block.NamePoolUsed;)
        {
            var length = (int)Math.Min(PoolChunkBytes, block.NamePoolUsed - offset);
            chunks.Add(block.GetNamePoolBytes(offset, length).ToArray());
            offset += (uint)length;
        }
        return new BlockRowSnapshot(
            [.. block.Rows[..rowCount]],
            [.. block.SequenceNumbers[..rowCount]],
            [.. chunks],
            [.. block.NameEntries[..checked((int)block.NameEntryCount + 1)]],
            block.NamePoolUsed,
            block.Header.LiveRowCount);
    }

    /// <summary>Writes the snapshot into a fresh, empty <paramref name="target" /> large enough to hold it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target" /> is too small or already holds rows.</exception>
    public void RestoreInto(BlockFile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var access = target.TakeAccess();
        if (target.RowCount != 0 || target.NamePoolUsed != 0 || target.NameEntryCount != 0)
        {
            throw new ArgumentException("A snapshot restores only into an empty block.", nameof(target));
        }

        if (target.SlotCapacity < RowCount || target.NamePoolCapacity < NamePoolUsed ||
            target.NameEntryCapacity < _nameEntries.Length - 1)
        {
            throw new ArgumentException("The target block is smaller than the snapshot.", nameof(target));
        }

        _rows.CopyTo(target.Rows);
        _sequenceNumbers.CopyTo(target.SequenceNumbers);
        uint offset = 0;
        foreach (var chunk in _names)
        {
            chunk.CopyTo(target.GetNamePoolBytes(offset, chunk.Length));
            offset += (uint)chunk.Length;
        }
        _nameEntries.CopyTo(target.NameEntries);
        target.PublishNamePoolUsed(NamePoolUsed);
        target.PublishNameEntryCount((uint)_nameEntries.Length - 1);
        NamePool.RebuildBuckets(target);
        target.Header.LiveRowCount = _liveRowCount;
        target.PublishRowCount(RowCount);
    }
}
