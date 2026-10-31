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
    ///     <paramref name="indices" /> contains a null element or lists the same <see cref="RootKey" /> more than once, or
    ///     lists two roots that resolve to the same directory at open (an alias such as a junction or symbolic link).
    ///     Members whose roots nest are accepted; see <see cref="Search" /> for which member answers.
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
                throw new ArgumentException($"Root {index.RootKey} is listed more than once.", nameof(indices));
            }
        }

        RejectSharedResolvedRoots(copy);

        _indices = copy;
        Indices = Array.AsReadOnly(copy);
    }

    // A member can serve a query now when it has a block it could lend: Status.Block is null for an Offline root, a
    // failed root without a retained block, and a directory view whose directory is out of the block.
    static bool IsAvailable(FileIndex index) => index.Status.Block is not null;

    // Whether outer's root contains inner's root, by the matcher Find uses, so queries and lookups agree on segment
    // boundaries, case folding and real-path rules.
    static bool Contains(FileIndex outer, FileIndex inner)
    {
        string? foldedPath = null;
        return LookupEngine.TryMatchRootPrefix(outer.Kind, outer.MatchableRootDirectoryPath, inner.RootDirectory,
            ref foldedPath, out _);
    }

    // The available members that sit under no other available member, in list order. Two members whose roots contain
    // each other keep the earlier one. Pairwise over the members, once per query; no per-row cost.
    FileIndex[] SelectOutermostAvailable()
    {
        var available = _indices.Where(IsAvailable).ToArray();
        var selected = new List<FileIndex>(available.Length);
        for (var position = 0; position < available.Length; position++)
        {
            var candidate = available[position];
            var covered = false;
            for (var other = 0; other < available.Length && !covered; other++)
            {
                covered = other != position && Contains(available[other], candidate) &&
                          (other < position || !Contains(candidate, available[other]));
            }

            if (!covered)
            {
                selected.Add(candidate);
            }
        }

        return [.. selected];
    }

    // Two keys that name one folder through an alias (junction, symbolic link, 8.3 name) resolve to the same final path
    // at open; they are duplicates exactly as two equal keys are. Equality is mutual containment by the lookup matcher.
    static void RejectSharedResolvedRoots(FileIndex[] indices)
    {
        for (var first = 0; first < indices.Length; first++)
        {
            for (var second = first + 1; second < indices.Length; second++)
            {
                if (Contains(indices[first], indices[second]) && Contains(indices[second], indices[first]))
                {
                    throw new ArgumentException(
                        $"Roots {indices[first].RootKey} and {indices[second].RootKey} resolve to the same directory " +
                        $"{indices[first].RootDirectory}; the indexes of a FileIndexSet must not repeat a root.",
                        nameof(indices));
                }
            }
        }
    }

    /// <summary>The member indices, in the order every set query visits them.</summary>
    public IReadOnlyList<FileIndex> Indices { get; }

    /// <summary>
    ///     Resolves a native filesystem path against the member indices. The outermost available member whose root
    ///     contains <paramref name="path" /> is selected (the same member a set query attributes the file to), and
    ///     the lookup is delegated to that index alone, with no fallback to another member on a miss.
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

        if (FindOutermostMatchingIndex(path) is not { } selected)
        {
            return null;
        }

        return selected.Find(path, cancellationToken);
    }

    /// <summary>
    ///     Materializes the whole match set across the outermost available members of <see cref="Indices" /> in list
    ///     order: a member that lies under another available member is not visited, so no file is returned twice
    ///     and each belongs to the outermost available member containing it. When
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

        if (query.Subtree is { NamedRootBlock: { } anchorBlock })
        {
            return ResolveSubtreeOwner(anchorBlock, cancellationToken).Search(query, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<FileEntry>();
        foreach (var index in SelectOutermostAvailable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(index.Search(query, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     Answers one exact-name search per requested name over the outermost available members in list order,
    ///     borrowing each member once for all names. Result <c>i</c> belongs to <paramref name="names" />[i] and,
    ///     absent concurrent mutation, equals <see cref="Search" /> for <paramref name="filter" /> with
    ///     <see cref="SearchQuery.NamePattern" /> set to that name and <see cref="SearchQuery.MatchMode" /> set to
    ///     <see cref="NameMatchMode.Exact" />: same members, same order, ascending row order within a member.
    ///     Duplicate names are answered independently.
    /// </summary>
    /// <param name="names">The names to find. An empty list returns an empty list.</param>
    /// <param name="filter">
    ///     Every predicate except the name; null means no other predicate. Its <see cref="SearchQuery.MatchMode" />
    ///     is ignored because the match is exact.
    /// </param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     An element of <paramref name="names" /> is null, <paramref name="filter" /> carries a
    ///     <see cref="SearchQuery.NamePattern" />, or <see cref="SearchQuery.Subtree" /> is the default
    ///     <see cref="FileEntry" />, belongs to an index outside this set or to a block a rescan replaced, or has
    ///     been superseded by a newer file reusing its row slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    ///     Any member index in <see cref="Indices" /> has been disposed, or the subtree anchor's block has been released.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public IReadOnlyList<IReadOnlyList<FileEntry>> SearchExactNames(IReadOnlyList<string> names,
        SearchQuery? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var position = 0;
        foreach (var name in names)
        {
            if (name is null)
            {
                throw new ArgumentException($"The name at index {position} is null.", nameof(names));
            }

            position++;
        }

        filter ??= new SearchQuery(null);
        if (filter.NamePattern is not null)
        {
            throw new ArgumentException(
                "The filter of an exact multi-name search carries no name pattern; the names are the pattern.",
                nameof(filter));
        }

        SubtreeAnchor.ThrowIfDefault(filter);
        ThrowIfAnyDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (names.Count == 0)
        {
            return [];
        }

        if (filter.Subtree is { NamedRootBlock: { } anchorBlock })
        {
            return ResolveSubtreeOwner(anchorBlock, cancellationToken)
                .SearchExactNames(names, filter, cancellationToken);
        }

        var results = names.Select(static _ => new List<FileEntry>()).ToArray();

        foreach (var index in SelectOutermostAvailable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var perMember = index.SearchExactNames(names, filter, cancellationToken);
            foreach (var (merged, member) in results.Zip(perMember))
            {
                merged.AddRange(member);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     The streaming form of <see cref="Search" />: visits the outermost available members in list order,
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
    ///     Enumerates the rows matching <paramref name="query" /> across the outermost available members in list order
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
        if (query.Subtree is { NamedRootBlock: { } anchorBlock })
        {
            return ResolveSubtreeOwner(anchorBlock, cancellationToken)
                .BeginRowEnumeration(query, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new IndexRowEnumerator(new RowEnumerationSegments(SelectOutermostAvailable(), cancellationToken), query);
    }

    IEnumerable<FileEntry> EnumerateCore(SearchQuery query, CancellationToken cancellationToken)
    {
        ThrowIfAnyDisposed();
        if (query.Subtree is { NamedRootBlock: { } anchorBlock })
        {
            foreach (var entry in ResolveSubtreeOwner(anchorBlock, cancellationToken).Enumerate(query, cancellationToken))
            {
                yield return entry;
            }

            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var index in SelectOutermostAvailable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in index.Enumerate(query, cancellationToken))
            {
                yield return entry;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    FileIndex ResolveSubtreeOwner(RootBlock anchorBlock, CancellationToken cancellationToken)
    {
        foreach (var index in _indices)
        {
            if (index.RootKey == anchorBlock.Root)
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

    FileIndex? FindOutermostMatchingIndex(string nativePath)
    {
        if (nativePath.Length == 0)
        {
            return null;
        }

        string? foldedPath = null;
        foreach (var candidate in SelectOutermostAvailable())
        {
            if (LookupEngine.TryMatchRootPrefix(candidate.Kind, candidate.MatchableRootDirectoryPath, nativePath,
                    ref foldedPath, out _))
            {
                return candidate;
            }
        }

        return null;
    }
}
