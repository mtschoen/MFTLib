namespace MFTLib.Index;

/// <summary>
///     A managed copy of an unfinished block's used rows, sequence numbers and name pool. Row name
///     offsets are relative to the name pool, so restoring the three regions into a larger block keeps
///     every row index, parent link and name valid. An enumeration walk uses it to grow its block in
///     place of starting the walk again.
/// </summary>
internal sealed class BlockRowSnapshot
{
    readonly FileRow[] _rows;
    readonly ushort[] _sequenceNumbers;
    readonly char[] _names;
    readonly uint _liveRowCount;

    BlockRowSnapshot(FileRow[] rows, ushort[] sequenceNumbers, char[] names, uint namePoolUsed, uint liveRowCount)
    {
        _rows = rows;
        _sequenceNumbers = sequenceNumbers;
        _names = names;
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
        var header = block.Header;
        var rowCount = (int)header.RowCount;
        var nameCharacters = (int)(header.NamePoolUsed / sizeof(char));
        return new BlockRowSnapshot(
            block.Rows[..rowCount].ToArray(),
            block.SequenceNumbers[..rowCount].ToArray(),
            block.NamePoolCharacters[..nameCharacters].ToArray(),
            header.NamePoolUsed,
            header.LiveRowCount);
    }

    /// <summary>Writes the snapshot into a fresh, empty <paramref name="target" /> large enough to hold it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target" /> is too small or already holds rows.</exception>
    public void RestoreInto(BlockFile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var access = target.TakeAccess();
        ref var header = ref target.Header;
        if (header.RowCount != 0 || header.NamePoolUsed != 0)
        {
            throw new ArgumentException("A snapshot restores only into an empty block.", nameof(target));
        }

        if (header.SlotCapacity < RowCount || header.NamePoolCapacity < NamePoolUsed)
        {
            throw new ArgumentException("The target block is smaller than the snapshot.", nameof(target));
        }

        _rows.CopyTo(target.Rows);
        _sequenceNumbers.CopyTo(target.SequenceNumbers);
        _names.CopyTo(target.NamePoolCharacters);
        header.NamePoolUsed = NamePoolUsed;
        header.LiveRowCount = _liveRowCount;
        header.RowCount = RowCount;
    }
}
