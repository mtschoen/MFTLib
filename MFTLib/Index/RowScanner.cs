namespace MFTLib.Index;

/// <summary>
///     A ref-struct enumerator over one root block's rows and names, working directly on the
///     mapped spans with no allocation. Internal by design (MFTLib#122): the public query
///     surface is lists, streaming <see cref="FileIndex.Enumerate" /> and row views from
///     <see cref="FileIndex.EnumerateRows" />. Their engines use this scanner. The range constructor partitions
///     one root across threads.
/// </summary>
ref struct RowScanner
{
    /// <summary>
    ///     How many rows a scan may read between two reads of its cancellation token. Small
    ///     enough that a cancelled query leaves the mapping promptly, large enough that the check
    ///     costs nothing next to the row work it guards.
    /// </summary>
    internal const uint CancellationCheckIntervalRows = 4096;

    readonly ReadOnlySpan<FileRow> _rows;
    readonly BlockFile _block;
    readonly uint _endRowExclusive;
    readonly CancellationToken _cancellationToken;
    uint _rowsUntilCancellationCheck;
    bool _started;

    internal RowScanner(RootBlock rootBlock, CancellationToken cancellationToken = default)
        : this(rootBlock, 0, uint.MaxValue, cancellationToken)
    {
    }

    internal RowScanner(RootBlock rootBlock, uint startRow, uint endRowExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootBlock);
        var block = rootBlock.Block;
        _rows = block.Rows;
        _block = block;
        _endRowExclusive = Math.Min(endRowExclusive, block.Header.RowCount);
        _cancellationToken = cancellationToken;

        // One, not the interval: the first row a scanner produces is itself a checkpoint, so a
        // query whose token was already cancelled stops before reading a row, and an engine that
        // opens one scanner per root block observes the token at least once for every block.
        _rowsUntilCancellationCheck = 1;
        CurrentRowIndex = startRow;
        _started = false;
    }

    internal uint CurrentRowIndex { get; private set; }

    public readonly ref readonly FileRow Current => ref _rows[(int)CurrentRowIndex];

    /// <summary>
    ///     The current row's name, taken from a single descriptor-word read so a concurrent
    ///     rename selects either the old immutable entry or the new one.
    /// </summary>
    internal readonly NameView CurrentName
    {
        get
        {
            var descriptor = FileRow.ReadDescriptorWord(in Current);
            return NamePool.Read(_block, FileRow.DescriptorNameId(descriptor));
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
            CurrentRowIndex++;
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

        return CurrentRowIndex < _endRowExclusive;
    }

    public readonly RowScanner GetEnumerator()
    {
        return this;
    }
}
