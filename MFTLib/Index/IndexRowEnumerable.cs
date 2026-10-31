using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     The result of <see cref="FileIndex.EnumerateRows" /> or <see cref="FileIndexSet.EnumerateRows" />,
///     consumed with <c>foreach</c>. The block borrow is taken by <see cref="GetEnumerator" /> and released
///     by the enumerator's <c>Dispose</c>, which <c>foreach</c> calls on normal completion, on <c>break</c>
///     and on an exception.
///     Each GetEnumerator takes a separate borrow, including on copies of this enumerable. It preserves
///     mapping ownership, not frozen row contents: a live watch can update rows during the pass.
///     Copies share one borrow and resource owner. Advancing a copy invalidates all older copies;
///     dispose through any copy to return the borrow and resources, invalidating all copies.
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
///     or a false MoveNext. Copies share one borrow and resource owner. Advancing through a copy invalidates
///     older copies: their Current and MoveNext throw InvalidOperationException. Dispose once through any copy.
///     Current and MoveNext on every copy then throw ObjectDisposedException naming IndexRowEnumerator;
///     repeated Dispose is harmless. A reachable undisposed enumerator can delay index disposal indefinitely.
///     The internal borrow has a finalizer that can return it once unreachable; collection timing is not guaranteed.
///     The borrow preserves mapping ownership, not row contents. A live watch mutates rows in place; captured
///     names are captured until the next advance, but other fields are live reads, with no atomic whole-row view.
///     Index disposal cancels an active borrowed scan at its cancellation checkpoint without returning its borrow.
/// </summary>
public ref struct IndexRowEnumerator
{
    readonly FileIndex.QueryScope _scope;
    readonly RowEnumerationSegments? _segments;
    readonly SearchQuery _query;
    readonly RootBlock? _singleBlock;
    readonly bool _queryNeedsOnlyFlags;
    bool _flagsOnly;
    readonly bool _includeDeleted;
    readonly bool _filterDirectories;
    readonly bool _wantDirectories;
    readonly ResolvedSubtree? _subtree;
    readonly uint? _admittedViewRow;
    CancellationToken _cancellationToken;
    RowScanner _scanner;
    CandidateCheckpoint _candidateCheckpoint;
    int _candidateCursor;
    RootBlock? _rootBlock;
    readonly RowEnumerationResources _resources;
    int _advanceVersion;
    ReadOnlySpan<char> _currentName;
    int _nextRoot;
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
        _singleBlock = scope.RootBlock;
        _admittedViewRow = scope.ViewRow;

        // A query with no name, size, date or subtree predicate needs only the row flags, so its loop
        // skips the general predicate and reads no query field per row. A directory view restricts rows to
        // its subtree, so a root bound to one never takes that loop (AdvanceRoot decides per root).
        _queryNeedsOnlyFlags = query.NamePattern is null && query.SizeMinimum is null && query.SizeMaximum is null &&
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
        _rootBlock = null;
        _resources = new RowEnumerationResources();
        _advanceVersion = 0;
        _currentName = default;
        _nextRoot = 0;
        _segment = 0;
    }

    internal IndexRowEnumerator(RowEnumerationSegments segments, SearchQuery query)
    {
        _scope = default;
        _segments = segments;
        _query = query;
        _cancellationToken = default;
        _singleBlock = null;
        _admittedViewRow = null;
        _queryNeedsOnlyFlags = query.NamePattern is null && query.SizeMinimum is null && query.SizeMaximum is null &&
                     query.LastWriteTimeMinimum is null && query.LastWriteTimeMaximum is null && query.Subtree is null;
        _includeDeleted = query.IncludeDeleted;
        _filterDirectories = query.IsDirectory.HasValue;
        _wantDirectories = query.IsDirectory.GetValueOrDefault();
        _subtree = null;
        _scanner = default;
        _rootBlock = null;
        _resources = new RowEnumerationResources();
        _advanceVersion = 0;
        _currentName = default;
        _nextRoot = 0;
        _segment = 0;
    }

    /// <summary>The current row, valid until the next <see cref="MoveNext" /> or <see cref="Dispose" />.</summary>
    /// <exception cref="ObjectDisposedException">This enumerator or a copy has returned the shared borrow.</exception>
    /// <exception cref="InvalidOperationException">There is no current row, or another copy has advanced.</exception>
    public readonly IndexRow Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            _resources.ValidateVersion(_advanceVersion);
            var rootBlock = _rootBlock ?? throw new InvalidOperationException("The enumerator has no current row.");
            return new IndexRow(rootBlock, _scanner.CurrentRowIndex, in _scanner.Current, _currentName);
        }
    }

    /// <summary>Advances to the next matching row, moving across root blocks as each is exhausted.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; observed before the first row and every 4096 rows.</exception>
    /// <exception cref="ObjectDisposedException">This enumerator or a copy has returned the shared borrow.</exception>
    /// <exception cref="InvalidDataException">A candidate's parent chain exceeds 128 hops while applying a subtree restriction.</exception>
    /// <exception cref="InvalidOperationException">Another copy of this enumerator has advanced.</exception>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        _advanceVersion = _resources.Advance(_advanceVersion);
        while (true)
        {
            if (_rootBlock is not null)
            {
                if (_resources.Candidates is { } candidates ? MoveToNextCandidateMatch(candidates) :
                    _flagsOnly ? MoveToNextFlagsMatch() : MoveToNextGeneralMatch())
                {
                    _currentName = _resources.Capture(_scanner.CurrentName);
                    return true;
                }

                _resources.EndRoot();
                _rootBlock = null;
            }

            if (!AdvanceRoot())
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

    bool MoveToNextGeneralMatch()
    {
        var names = _resources.Names;
        while (_scanner.MoveNext())
        {
            ref readonly var row = ref _scanner.Current;
            if (!SearchEngine.RowMatches(in row, names, _query))
            {
                continue;
            }

            if (_resources.Membership is { } membership && !membership.Contains(_scanner.CurrentRowIndex))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    ///     The exact-name form of the match loop: the candidate rows the postings named, ascending, each verified by one
    ///     read of the live row. The cadence counts every candidate, rejected ones included.
    /// </summary>
    bool MoveToNextCandidateMatch(PostingCandidates candidates)
    {
        var names = _resources.Names;
        var rows = candidates.Rows;
        while (_candidateCursor < rows.Count)
        {
            _candidateCheckpoint.Step();
            var rowIndex = rows[_candidateCursor++];
            if (rowIndex >= candidates.RowCount)
            {
                continue;
            }

            _scanner.MoveTo(rowIndex);
            ref readonly var row = ref _scanner.Current;
            if (!SearchEngine.RowMatches(in row, names, _query))
            {
                continue;
            }

            if (_resources.Membership is { } membership && !membership.Contains(rowIndex))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    void BeginCandidates()
    {
        _candidateCursor = 0;
        _candidateCheckpoint = _resources.Candidates?.BeginVerification() ?? default;
    }

    bool AdvanceRoot()
    {
        if (_segments is not null)
        {
            if (_segments.TryMoveNextSegment(out var nextBlock, out var segmentViewRow, out _cancellationToken, ref _segment))
            {
                _rootBlock = nextBlock;
                _scanner = new RowScanner(nextBlock, _cancellationToken);
                _flagsOnly = _queryNeedsOnlyFlags && !nextBlock.View.IsDirectoryView;
                _resources.BeginRoot(nextBlock, _query, null, segmentViewRow, _cancellationToken);
                BeginCandidates();
                return true;
            }

            _rootBlock = null;
            return false;
        }

        if (_nextRoot == 0)
        {
            _nextRoot = 1;
            _cancellationToken.ThrowIfCancellationRequested();
            if (_singleBlock is { } rootBlock)
            {
                _rootBlock = rootBlock;
                _scanner = new RowScanner(rootBlock, _cancellationToken);
                _flagsOnly = _queryNeedsOnlyFlags && !rootBlock.View.IsDirectoryView;
                _resources.BeginRoot(rootBlock, _query, _subtree, _admittedViewRow, _cancellationToken);
                BeginCandidates();
                return true;
            }
        }

        _nextRoot = int.MaxValue;
        _cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>Returns the shared borrow once, invalidating access through all copies. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_resources is null)
        {
            // default(IndexRowEnumerator) owns nothing.
            return;
        }

        _resources.Dispose();
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
