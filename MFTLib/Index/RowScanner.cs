namespace MFTLib.Index;

/// <summary>
///     A ref-struct enumerator over one drive block's rows and names, working directly on the
///     mapped spans with no allocation. This is the internal scan escape hatch: the public
///     query surface returns lists, and this is what those queries are built on. The range
///     constructor is how a query partitions one drive across threads.
/// </summary>
internal ref struct RowScanner
{
    /// <summary>
    ///     How many rows a scan may read between two reads of its cancellation token. Small
    ///     enough that a cancelled query leaves the mapping promptly, large enough that the check
    ///     costs nothing next to the row work it guards.
    /// </summary>
    internal const uint CancellationCheckIntervalRows = 4096;

    readonly ReadOnlySpan<FileRow> _rows;
    readonly ReadOnlySpan<char> _namePool;
    readonly uint _endRowExclusive;
    readonly CancellationToken _cancellationToken;
    uint _rowsUntilCancellationCheck;
    uint _currentRowIndex;
    bool _started;

    internal RowScanner(Snapshot snapshot, ushort driveOrdinal,
        CancellationToken cancellationToken = default)
        : this(snapshot, driveOrdinal, 0, uint.MaxValue, cancellationToken)
    {
    }

    internal RowScanner(Snapshot snapshot, ushort driveOrdinal, uint startRow, uint endRowExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var block = snapshot.GetDriveBlock(driveOrdinal).Block;
        _rows = block.Rows;
        _namePool = block.NamePoolCharacters;
        _endRowExclusive = Math.Min(endRowExclusive, block.Header.RowCount);
        _cancellationToken = cancellationToken;

        // One, not the interval: the first row a scanner produces is itself a checkpoint, so a
        // query whose token was already cancelled stops before reading a row, and an engine that
        // opens one scanner per drive block observes the token at least once for every block.
        _rowsUntilCancellationCheck = 1;
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

    /// <summary>
    ///     Advances to the next row, reading the cancellation token on the first call and then
    ///     once every <see cref="CancellationCheckIntervalRows" /> calls, including the call that
    ///     finds the range exhausted. Every scanning engine inherits cancellation from here
    ///     rather than counting rows of its own.
    /// </summary>
    /// <exception cref="OperationCanceledException">The query's token was cancelled.</exception>
    public bool MoveNext()
    {
        if (_started)
        {
            _currentRowIndex++;
        }

        _started = true;

        // Counted before the range is tested, not after, so a scanner whose range holds no rows
        // still reads the token once and the per-block half of the contract holds for an empty
        // block too. The loop pays the same decrement and branch either way.
        _rowsUntilCancellationCheck--;
        if (_rowsUntilCancellationCheck == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _rowsUntilCancellationCheck = CancellationCheckIntervalRows;
        }

        return _currentRowIndex < _endRowExclusive;
    }

    public readonly RowScanner GetEnumerator()
    {
        return this;
    }
}
