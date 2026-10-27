namespace MFTLib.Index;

/// <summary>
///     Queries across an ordered set of <see cref="FileIndex" /> instances. Each member index owns
///     one root, its block, its watch and its disposal; this set coordinates point lookups and
///     row scans across them without owning their lifecycle.
/// </summary>
public sealed class FileIndexSet
{
    readonly FileIndex[] _indices;

    /// <summary>
    ///     Creates a query set over <paramref name="indices" />, copying the list once in caller order.
    /// </summary>
    /// <param name="indices">The member indices to query across.</param>
    /// <exception cref="ArgumentNullException"><paramref name="indices" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="indices" /> contains a null element or lists the same <see cref="RootKey" /> more than once.
    /// </exception>
    public FileIndexSet(IReadOnlyList<FileIndex> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var copy = indices.ToArray();
        HashSet<RootKey> seen = [];
        foreach (var index in copy)
        {
            if (index is null)
            {
                throw new ArgumentException("FileIndexSet indices must not contain null entries.", nameof(indices));
            }

            if (!seen.Add(index.RootKey))
            {
                throw new ArgumentException($"Drive {index.RootKey} is listed more than once.", nameof(indices));
            }
        }

        _indices = copy;
        Indices = Array.AsReadOnly(copy);
    }

    /// <summary>The member indices, in the order every set query visits them.</summary>
    public IReadOnlyList<FileIndex> Indices { get; }

    /// <summary>
    ///     Resolves a native filesystem path against the member indices. The member whose root
    ///     directory is the longest matching prefix of <paramref name="path" /> is selected, and
    ///     the lookup is delegated to that index alone, with no fallback to an outer root on a miss.
    /// </summary>
    /// <param name="path">The path to resolve, in the form <see cref="FileEntry.Path" /> emits.</param>
    /// <param name="cancellationToken">Stops the walk before the first row and at least every 4096 rows.</param>
    /// <returns>The matching entry, or null when no member root matches or the selected root has no matching entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">Any member index in <see cref="Indices" /> has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public FileEntry? Find(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfAnyDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (FindLongestMatchingIndex(path) is not { } selected)
        {
            return null;
        }

        return selected.Find(path, cancellationToken);
    }

    /// <summary>
    ///     Materializes the whole match set across <see cref="Indices" /> in list order. When
    ///     <see cref="SearchQuery.Subtree" /> is set, only the member index owning that anchor is
    ///     borrowed and scanned.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="query" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="query" /> has an undefined <see cref="SearchQuery.MatchMode" />.</exception>
    /// <exception cref="ArgumentException">
    ///     <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />, belongs to an index outside
    ///     this set or to a block a rescan replaced, or has been superseded by a newer file reusing its row slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    ///     Any member index in <see cref="Indices" /> has been disposed, or the subtree anchor's block has been released.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public IReadOnlyList<FileEntry> Search(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        NameMatching.ThrowIfUndefined(query);
        SubtreeAnchor.ThrowIfDefault(query);
        ThrowIfAnyDisposed();

        if (query.Subtree is { NamedDriveBlock: { } anchorBlock })
        {
            return ResolveSubtreeOwner(anchorBlock, cancellationToken).Search(query, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<FileEntry>();
        foreach (var index in _indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(index.Search(query, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     The streaming form of <see cref="Search" />: visits <see cref="Indices" /> in list order,
    ///     borrowing one member index at a time and releasing its borrow before advancing to the next.
    ///     When <see cref="SearchQuery.Subtree" /> is set, only the member index owning that anchor is
    ///     borrowed and scanned.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="ArgumentNullException">At call time: <paramref name="query" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">At call time: <paramref name="query" /> has an undefined <see cref="SearchQuery.MatchMode" />.</exception>
    /// <exception cref="ArgumentException">
    ///     At call time: <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />. From the first
    ///     MoveNext: the anchor belongs to an index outside this set or to a block a rescan replaced, or has been
    ///     superseded by a newer file reusing its row slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    ///     At call time or during iteration: any member index in <see cref="Indices" /> has been disposed, or the
    ///     subtree anchor's block has been released.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public IEnumerable<FileEntry> Enumerate(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        NameMatching.ThrowIfUndefined(query);
        SubtreeAnchor.ThrowIfDefault(query);
        ThrowIfAnyDisposed();
        return EnumerateCore(query, cancellationToken);
    }

    /// <summary>
    ///     Enumerates the rows matching <paramref name="query" /> across <see cref="Indices" /> in list order
    ///     as non-allocating <see cref="IndexRow" /> views, borrowing one member index at a time and releasing
    ///     its borrow before advancing to the next.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Observed before the first row and then at least every 4096 rows.</param>
    /// <exception cref="ArgumentNullException">At call time: <paramref name="query" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">At call time: <paramref name="query" /> has an undefined <see cref="SearchQuery.MatchMode" />.</exception>
    /// <exception cref="ArgumentException">
    ///     At call time: <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />. At
    ///     GetEnumerator: the anchor belongs to an index outside this set or to a block a rescan replaced, or has
    ///     been superseded by a newer file reusing its row slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    ///     At GetEnumerator or MoveNext: any member index in <see cref="Indices" /> has been disposed, the subtree
    ///     anchor's block has been released, or this enumeration's shared borrow has been returned.
    /// </exception>
    /// <exception cref="OperationCanceledException">At GetEnumerator or MoveNext: caller cancellation or member disposal cancels the enumeration.</exception>
    public IndexRowEnumerable EnumerateRows(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        NameMatching.ThrowIfUndefined(query);
        SubtreeAnchor.ThrowIfDefault(query);
        return new IndexRowEnumerable(this, query, cancellationToken);
    }

    internal IndexRowEnumerator BeginRowEnumeration(SearchQuery query, CancellationToken cancellationToken)
    {
        ThrowIfAnyDisposed();
        if (query.Subtree is { NamedDriveBlock: { } anchorBlock })
        {
            return ResolveSubtreeOwner(anchorBlock, cancellationToken)
                .BeginRowEnumeration(query, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new IndexRowEnumerator(new RowEnumerationSegments(_indices, cancellationToken), query);
    }

    IEnumerable<FileEntry> EnumerateCore(SearchQuery query, CancellationToken cancellationToken)
    {
        ThrowIfAnyDisposed();
        if (query.Subtree is { NamedDriveBlock: { } anchorBlock })
        {
            foreach (var entry in ResolveSubtreeOwner(anchorBlock, cancellationToken).Enumerate(query, cancellationToken))
            {
                yield return entry;
            }

            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var index in _indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in index.Enumerate(query, cancellationToken))
            {
                yield return entry;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    FileIndex ResolveSubtreeOwner(DriveBlock anchorBlock, CancellationToken cancellationToken)
    {
        foreach (var index in _indices)
        {
            if (index.RootKey == anchorBlock.Drive)
            {
                return index;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw SubtreeAnchor.ForeignOrDisposed(anchorBlock, "query");
    }

    void ThrowIfAnyDisposed()
    {
        foreach (var index in _indices)
        {
            index.ThrowIfDisposed();
        }
    }

    FileIndex? FindLongestMatchingIndex(string nativePath)
    {
        if (nativePath.Length == 0)
        {
            return null;
        }

        string? foldedPath = null;
        FileIndex? best = null;
        var bestLength = -1;
        foreach (var candidate in _indices)
        {
            if (!LookupEngine.TryMatchRootPrefix(candidate.Kind, candidate.MatchableRootDirectoryPath, nativePath,
                    ref foldedPath, out var rootLength))
            {
                continue;
            }

            if (rootLength > bestLength)
            {
                best = candidate;
                bestLength = rootLength;
            }
        }

        return best;
    }
}
