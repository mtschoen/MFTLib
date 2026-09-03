namespace MFTLib.Index;

/// <summary>
///     A ref-struct enumerator over one drive block's rows and names, working directly on the
///     mapped spans with no allocation. This is the internal scan escape hatch: the public
///     query surface returns lists, and this is what those queries are built on. The range
///     constructor is how a query partitions one drive across threads.
/// </summary>
internal ref struct RowScanner
{
    readonly ReadOnlySpan<FileRow> _rows;
    readonly ReadOnlySpan<char> _namePool;
    readonly uint _endRowExclusive;
    uint _currentRowIndex;
    bool _started;

    internal RowScanner(Snapshot snapshot, ushort driveOrdinal)
        : this(snapshot, driveOrdinal, 0, uint.MaxValue)
    {
    }

    internal RowScanner(Snapshot snapshot, ushort driveOrdinal, uint startRow, uint endRowExclusive)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var block = snapshot.GetDriveBlock(driveOrdinal).Block;
        _rows = block.Rows;
        _namePool = block.NamePoolCharacters;
        _endRowExclusive = Math.Min(endRowExclusive, block.Header.RowCount);
        _currentRowIndex = startRow;
        _started = false;
    }

    internal readonly uint CurrentRowIndex => _currentRowIndex;

    public readonly ref readonly FileRow Current => ref _rows[(int)_currentRowIndex];

    /// <summary>
    ///     The current row's name, taken from a single descriptor-word read so a concurrent
    ///     rename cannot pair a new name offset with an old name length.
    /// </summary>
    internal readonly ReadOnlySpan<char> CurrentName
    {
        get
        {
            var descriptor = FileRow.ReadDescriptorWord(in Current);
            var start = (int)(FileRow.DescriptorNameOffsetBytes(descriptor) / sizeof(char));
            return _namePool.Slice(start, FileRow.DescriptorNameLengthUnits(descriptor));
        }
    }

    public bool MoveNext()
    {
        if (_started)
        {
            _currentRowIndex++;
        }

        _started = true;
        return _currentRowIndex < _endRowExclusive;
    }

    public readonly RowScanner GetEnumerator()
    {
        return this;
    }
}
