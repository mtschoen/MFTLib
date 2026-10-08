using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     The result of <see cref="FileIndex.EnumerateRows" />: a ref struct consumed with <c>foreach</c>. The
///     snapshot borrow is taken by <see cref="GetEnumerator" /> and released by the enumerator's
///     <c>Dispose</c>, which <c>foreach</c> calls on normal completion, <c>break</c> and exception.
/// </summary>
public readonly ref struct IndexRowEnumerable
{
    readonly FileIndex _index;
    readonly SearchQuery _query;
    readonly CancellationToken _cancellationToken;

    internal IndexRowEnumerable(FileIndex index, SearchQuery query, CancellationToken cancellationToken)
    {
        _index = index;
        _query = query;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Takes the borrow and returns the enumerator.</summary>
    public IndexRowEnumerator GetEnumerator() => _index.BeginRowEnumeration(_query, _cancellationToken);
}

/// <summary>Enumerator over matching rows. Must be disposed; <c>foreach</c> does that.</summary>
public ref struct IndexRowEnumerator
{
    readonly FileIndex.QueryScope _scope;
    readonly Snapshot _snapshot;
    readonly SearchQuery _query;
    readonly CancellationToken _cancellationToken;
    readonly bool _isSimple;
    readonly bool _includeDeleted;
    readonly bool _filterDirectories;
    readonly bool _wantDirectories;
    readonly uint _underRow;
    readonly IReadOnlyList<DriveBlock> _driveBlocks;
    RowScanner _scanner;
    DriveBlock? _driveBlock;
    int _nextDrive;
    bool _disposed;

    internal IndexRowEnumerator(FileIndex.QueryScope scope, SearchQuery query, bool useGeneralLoop)
    {
        _scope = scope;
        _snapshot = scope.Snapshot;
        _query = query;
        _cancellationToken = scope.CancellationToken;
        _driveBlocks = _snapshot.DriveBlocks;
        _isSimple = !useGeneralLoop && query.NamePattern is null && query.MinimumSize is null &&
                    query.MaximumSize is null && query.ModifiedAfter is null && query.ModifiedBefore is null &&
                    query.Under is null;
        _includeDeleted = query.IncludeDeleted;
        _filterDirectories = query.Directories.HasValue;
        _wantDirectories = query.Directories.GetValueOrDefault();
        _underRow = query.Under?.RowIndex ?? 0;
        _scanner = default;
        _driveBlock = null;
        _nextDrive = 0;
        _disposed = false;
        if (query.Under is { } ancestor && !ancestor.IsValid)
        {
            _nextDrive = int.MaxValue;
        }
    }

    /// <summary>The current row, valid until the next <see cref="MoveNext" />.</summary>
    public readonly IndexRow Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var driveBlock = _driveBlock!;
#if LEAN_INDEX_ROW
            return new IndexRow(_snapshot, driveBlock.DriveOrdinal, _scanner.CurrentRowIndex, in _scanner.Current,
                _scanner.CurrentName);
#else
            return new IndexRow(_snapshot, driveBlock.DriveLetter, driveBlock.DriveOrdinal, _scanner.CurrentRowIndex,
                in _scanner.Current, _scanner.CurrentName);
#endif
        }
    }

    /// <summary>Advances to the next matching row across the drive blocks.</summary>
    public bool MoveNext()
    {
        while (true)
        {
            if (_driveBlock is not null)
            {
                if (_isSimple)
                {
                    while (_scanner.MoveNext())
                    {
                        ref readonly var row = ref _scanner.Current;
                        if (!row.IsInUse || (!_includeDeleted && row.IsDeleted) ||
                            (_filterDirectories && row.IsDirectory != _wantDirectories))
                        {
                            continue;
                        }

                        return true;
                    }
                }
                else
                {
                    while (_scanner.MoveNext())
                    {
                        ref readonly var row = ref _scanner.Current;
                        if (!SearchEngine.RowMatches(in row, _scanner.CurrentName, _query))
                        {
                            continue;
                        }

                        if (_query.Under is not null &&
                            !IndexNavigation.IsUnder(_driveBlock.Block, _scanner.CurrentRowIndex, _underRow))
                        {
                            continue;
                        }

                        return true;
                    }
                }

                _driveBlock = null;
            }

            if (!AdvanceDrive())
            {
                return false;
            }
        }
    }

    bool AdvanceDrive()
    {
        while (_nextDrive < _driveBlocks.Count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var driveBlock = _driveBlocks[_nextDrive++];
            if (_query.Under is { } under && (!under.IsValid || under.DriveOrdinal != driveBlock.DriveOrdinal))
            {
                continue;
            }

            _driveBlock = driveBlock;
            _scanner = new RowScanner(_snapshot, driveBlock.DriveOrdinal, _cancellationToken);
            return true;
        }

        _nextDrive = int.MaxValue;
        _cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>Releases the snapshot borrow. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scope.Dispose();
    }
}
