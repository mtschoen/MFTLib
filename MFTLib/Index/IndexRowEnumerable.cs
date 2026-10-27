using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     The result of <see cref="FileIndex.EnumerateRows" /> or <see cref="FileIndexSet.EnumerateRows" />,
///     consumed with <c>foreach</c>. The block borrow is taken by <see cref="GetEnumerator" /> and released
///     by the enumerator's <c>Dispose</c>, which <c>foreach</c> calls on normal completion, on <c>break</c>
///     and on an exception.
///     Each GetEnumerator takes a separate borrow, including on copies of this enumerable. It preserves
///     mapping ownership, not frozen row contents: a live watch can update rows during the pass.
///     Copies of the returned enumerator share its borrow; dispose it once, invalidating all copies.
///     Their Current and MoveNext then throw ObjectDisposedException; repeated Dispose is harmless.
///     A reachable undisposed enumerator can delay index disposal indefinitely. Its internal borrow has
///     a finalizer that can return an unreachable borrow, but collection timing is not guaranteed.
/// </summary>
public readonly ref struct IndexRowEnumerable
{
    readonly object _source;
    readonly SearchQuery _query;
    readonly CancellationToken _cancellationToken;

    internal IndexRowEnumerable(FileIndex index, SearchQuery query, CancellationToken cancellationToken)
    {
        _source = index;
        _query = query;
        _cancellationToken = cancellationToken;
    }

    internal IndexRowEnumerable(FileIndexSet indexSet, SearchQuery query, CancellationToken cancellationToken)
    {
        _source = indexSet;
        _query = query;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Takes the block borrow and returns the enumerator.</summary>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> (or a member of <see cref="FileIndexSet" />) has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    public IndexRowEnumerator GetEnumerator() =>
        _source is FileIndexSet indexSet
            ? indexSet.BeginRowEnumeration(_query, _cancellationToken)
            : ((FileIndex)_source).BeginRowEnumeration(_query, _cancellationToken);
}

/// <summary>
///     Enumerates the rows matching a query. It holds a block borrow until <see cref="Dispose" />; a
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
    readonly RowEnumerationSegments? _segments;
    readonly SearchQuery _query;
    readonly DriveBlock? _singleBlock;
    readonly bool _flagsOnly;
    readonly bool _includeDeleted;
    readonly bool _filterDirectories;
    readonly bool _wantDirectories;
    readonly ResolvedSubtree? _subtree;
    CancellationToken _cancellationToken;
    RowScanner _scanner;
    DriveBlock? _driveBlock;
    int _nextDrive;
    int _segment;

    /// <summary>
    ///     Takes ownership of <paramref name="scope" />: it is returned if the query's subtree anchor is refused,
    ///     so a failed <c>GetEnumerator</c> leaves no borrow behind.
    /// </summary>
    internal IndexRowEnumerator(FileIndex.QueryScope scope, SearchQuery query)
    {
        _scope = scope;
        _segments = null;
        _query = query;
        _cancellationToken = scope.CancellationToken;
        _singleBlock = scope.DriveBlock;

        // A query with no name, size, date or subtree predicate needs only the row flags, so its loop
        // skips the general predicate and reads no query field per row.
        _flagsOnly = query.NamePattern is null && query.SizeMinimum is null && query.SizeMaximum is null &&
                     query.LastWriteTimeMinimum is null && query.LastWriteTimeMaximum is null && query.Subtree is null;
        _includeDeleted = query.IncludeDeleted;
        _filterDirectories = query.IsDirectory.HasValue;
        _wantDirectories = query.IsDirectory.GetValueOrDefault();
        try
        {
            _subtree = SubtreeAnchor.Resolve(_singleBlock, query);
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        _scanner = default;
        _driveBlock = null;
        _nextDrive = 0;
        _segment = 0;
    }

    internal IndexRowEnumerator(RowEnumerationSegments segments, SearchQuery query)
    {
        _scope = default;
        _segments = segments;
        _query = query;
        _cancellationToken = default;
        _singleBlock = null;
        _flagsOnly = query.NamePattern is null && query.SizeMinimum is null && query.SizeMaximum is null &&
                     query.LastWriteTimeMinimum is null && query.LastWriteTimeMaximum is null && query.Subtree is null;
        _includeDeleted = query.IncludeDeleted;
        _filterDirectories = query.IsDirectory.HasValue;
        _wantDirectories = query.IsDirectory.GetValueOrDefault();
        _subtree = null;
        _scanner = default;
        _driveBlock = null;
        _nextDrive = 0;
        _segment = 0;
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
            return new IndexRow(driveBlock, _scanner.CurrentRowIndex, in _scanner.Current, _scanner.CurrentName);
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

            if (_subtree is { } under &&
                !IndexNavigation.IsUnder(driveBlock.Block, _scanner.CurrentRowIndex, under.RowIndex))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    bool AdvanceDrive()
    {
        if (_segments is not null)
        {
            if (_segments.TryMoveNextSegment(out var nextBlock, out _cancellationToken, ref _segment))
            {
                _driveBlock = nextBlock;
                _scanner = new RowScanner(nextBlock, _cancellationToken);
                return true;
            }

            _driveBlock = null;
            return false;
        }

        if (_nextDrive == 0)
        {
            _nextDrive = 1;
            _cancellationToken.ThrowIfCancellationRequested();
            if (_singleBlock is { } driveBlock)
            {
                _driveBlock = driveBlock;
                _scanner = new RowScanner(driveBlock, _cancellationToken);
                return true;
            }
        }

        _nextDrive = int.MaxValue;
        _cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>Returns the shared borrow once, invalidating access through all copies. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_segments is not null)
        {
            _segments.Dispose();
            return;
        }

        if (_scope.IsDisposed)
        {
            return;
        }

        _scope.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly void ThrowIfDisposed()
    {
        if (_segments is not null)
        {
            if (_segments.IsDisposed || _segments.ActiveSegment != _segment)
            {
                throw new ObjectDisposedException(nameof(IndexRowEnumerator));
            }

            return;
        }

        if (_scope.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(IndexRowEnumerator));
        }
    }
}

/// <summary>
///     Shared heap-allocated state for a multi-index <see cref="IndexRowEnumerator" /> over a
///     <see cref="FileIndexSet" />. Borrows at most one member index's block at a time, releasing
///     the current segment's borrow before borrowing the next.
/// </summary>
sealed class RowEnumerationSegments : IDisposable
{
    readonly IReadOnlyList<FileIndex> _indices;
    readonly CancellationToken _callerCancellationToken;
    FileIndex.QueryScope _activeScope;
    bool _hasActiveScope;
    bool _initialConsumed;
    int _nextIndex;
    int _activeSegment;
    int _disposed;

    internal RowEnumerationSegments(IReadOnlyList<FileIndex> indices, CancellationToken callerCancellationToken)
    {
        _indices = indices;
        _callerCancellationToken = callerCancellationToken;
        if (indices.Count > 0)
        {
            _activeScope = indices[0].BeginQuery(callerCancellationToken);
            _hasActiveScope = true;
            _nextIndex = 1;
        }
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal int ActiveSegment => Volatile.Read(ref _activeSegment);

    internal bool TryMoveNextSegment([NotNullWhen(true)] out DriveBlock? driveBlock, out CancellationToken cancellationToken,
        ref int enumeratorSegment)
    {
        while (true)
        {
            if (!_initialConsumed)
            {
                _initialConsumed = true;
                if (!_hasActiveScope)
                {
                    _callerCancellationToken.ThrowIfCancellationRequested();
                    driveBlock = null;
                    cancellationToken = default;
                    return false;
                }
            }
            else
            {
                var wasCancelled = _hasActiveScope && _activeScope.CancellationToken.IsCancellationRequested;
                var cancelledToken = wasCancelled ? _activeScope.CancellationToken : default;
                ReleaseActiveScope();
                if (wasCancelled)
                {
                    throw new OperationCanceledException(cancelledToken);
                }

                if (_nextIndex >= _indices.Count)
                {
                    enumeratorSegment = _activeSegment;
                    _callerCancellationToken.ThrowIfCancellationRequested();
                    driveBlock = null;
                    cancellationToken = default;
                    return false;
                }

                _callerCancellationToken.ThrowIfCancellationRequested();
                _activeScope = _indices[_nextIndex++].BeginQuery(_callerCancellationToken);
                _hasActiveScope = true;
                enumeratorSegment = _activeSegment;
            }

            _activeScope.CancellationToken.ThrowIfCancellationRequested();
            if (_activeScope.DriveBlock is { } block)
            {
                driveBlock = block;
                cancellationToken = _activeScope.CancellationToken;
                return true;
            }
        }
    }

    void ReleaseActiveScope()
    {
        if (_hasActiveScope)
        {
            Interlocked.Increment(ref _activeSegment);
            _hasActiveScope = false;
            _activeScope.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseActiveScope();
        }
    }
}
