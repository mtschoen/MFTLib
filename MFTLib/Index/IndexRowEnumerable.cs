using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     The result of <see cref="FileIndex.EnumerateRows" />, consumed with <c>foreach</c>. The snapshot borrow
///     is taken by <see cref="GetEnumerator" /> and released by the enumerator's <c>Dispose</c>, which
///     <c>foreach</c> calls on normal completion, on <c>break</c> and on an exception.
///     Each GetEnumerator takes a separate borrow, including on copies of this enumerable. It preserves
///     mapping ownership, not frozen row contents: a live watch can update rows during the pass.
///     Copies of the returned enumerator share its borrow; dispose it once, invalidating all copies.
///     Their Current and MoveNext then throw ObjectDisposedException; repeated Dispose is harmless.
///     A reachable undisposed enumerator can delay index disposal indefinitely. Its internal borrow has
///     a finalizer that can return an unreachable borrow, but collection timing is not guaranteed.
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

    /// <summary>Takes the snapshot borrow and returns the enumerator.</summary>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    public IndexRowEnumerator GetEnumerator() => _index.BeginRowEnumeration(_query, _cancellationToken);
}

/// <summary>
///     Enumerates the rows matching a query. It holds a snapshot borrow until <see cref="Dispose" />; a
///     <c>foreach</c> disposes it, and a caller that obtains one by hand must dispose it even after an exception
///     or a false MoveNext. Copies share one borrow, not independent ownership: dispose it once through any
///     copy. Current and MoveNext on every copy then throw ObjectDisposedException naming IndexRowEnumerator;
///     repeated Dispose is harmless. A reachable undisposed enumerator can delay index disposal indefinitely.
///     The internal borrow has a finalizer that can return it once unreachable; collection timing is not guaranteed.
///     The borrow preserves mapping ownership, not row contents. A live watch mutates rows in place; captured
///     name spans keep their append-only text, but other fields are live reads, with no atomic whole-row view.
///     Index disposal cancels an active borrowed scan at its cancellation checkpoint without returning its borrow.
/// </summary>
public ref struct IndexRowEnumerator
{
    readonly FileIndex.QueryScope _scope;
    readonly Snapshot _snapshot;
    readonly SearchQuery _query;
    readonly CancellationToken _cancellationToken;
    readonly IReadOnlyList<DriveBlock> _driveBlocks;
    readonly bool _flagsOnly;
    readonly bool _includeDeleted;
    readonly bool _filterDirectories;
    readonly bool _wantDirectories;
    readonly uint _underRow;
    RowScanner _scanner;
    DriveBlock? _driveBlock;
    int _nextDrive;

    internal IndexRowEnumerator(FileIndex.QueryScope scope, SearchQuery query)
    {
        _scope = scope;
        _snapshot = scope.Snapshot;
        _query = query;
        _cancellationToken = scope.CancellationToken;
        _driveBlocks = _snapshot.DriveBlocks;

        // A query with no name, size, date or subtree predicate needs only the row flags, so its loop
        // skips the general predicate and reads no query field per row.
        _flagsOnly = query.NamePattern is null && query.MinimumSize is null && query.MaximumSize is null &&
                     query.ModifiedAfter is null && query.ModifiedBefore is null && query.Under is null;
        _includeDeleted = query.IncludeDeleted;
        _filterDirectories = query.Directories.HasValue;
        _wantDirectories = query.Directories.GetValueOrDefault();
        _underRow = query.Under?.RowIndex ?? 0;
        _scanner = default;
        _driveBlock = null;
        _nextDrive = query.Under is { IsValid: false } ? int.MaxValue : 0;
    }

    /// <summary>The current row, valid until the next <see cref="MoveNext" /> or <see cref="Dispose" />.</summary>
    /// <exception cref="ObjectDisposedException">This enumerator or a copy has returned the shared borrow.</exception>
    /// <exception cref="InvalidOperationException">There is no current row.</exception>
    public readonly IndexRow Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            var driveBlock = _driveBlock ?? throw new InvalidOperationException("The enumerator has no current row.");
            return new IndexRow(_snapshot, driveBlock.DriveLetter, driveBlock.DriveOrdinal, _scanner.CurrentRowIndex,
                in _scanner.Current, _scanner.CurrentName);
        }
    }

    /// <summary>Advances to the next matching row, moving across drive blocks as each is exhausted.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; observed before the first row and every 4096 rows.</exception>
    /// <exception cref="ObjectDisposedException">This enumerator or a copy has returned the shared borrow.</exception>
    /// <exception cref="InvalidDataException">A candidate's parent chain exceeds 128 hops while applying a subtree restriction.</exception>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        while (true)
        {
            if (_driveBlock is not null)
            {
                if (_flagsOnly ? MoveToNextFlagsMatch() : MoveToNextGeneralMatch(_driveBlock))
                {
                    return true;
                }

                _driveBlock = null;
            }

            if (!AdvanceDrive())
            {
                return false;
            }
        }
    }

    bool MoveToNextFlagsMatch()
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

        return false;
    }

    bool MoveToNextGeneralMatch(DriveBlock driveBlock)
    {
        while (_scanner.MoveNext())
        {
            ref readonly var row = ref _scanner.Current;
            if (!SearchEngine.RowMatches(in row, _scanner.CurrentName, _query))
            {
                continue;
            }

            if (_query.Under is not null &&
                !IndexNavigation.IsUnder(driveBlock.Block, _scanner.CurrentRowIndex, _underRow))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    bool AdvanceDrive()
    {
        while (_nextDrive < _driveBlocks.Count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var driveBlock = _driveBlocks[_nextDrive++];
            if (_query.Under is { } under && under.DriveOrdinal != driveBlock.DriveOrdinal)
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

    /// <summary>Returns the shared borrow once, invalidating access through all copies. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_scope.IsDisposed)
        {
            return;
        }

        _scope.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly void ThrowIfDisposed()
    {
        if (_scope.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(IndexRowEnumerator));
        }
    }
}
