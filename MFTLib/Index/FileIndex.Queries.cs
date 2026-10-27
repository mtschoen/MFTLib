namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Resolves a native filesystem path to its entry. When the indexed root directory is a
    ///     prefix of the path, the remaining segments are walked down from the block's root row,
    ///     one name per level. This is the inverse of <see cref="FileEntry.Path" /> for live entries:
    ///     deleted entries are skipped, so their paths do not resolve.
    /// </summary>
    /// <param name="path">The path to resolve, in the form <see cref="FileEntry.Path" /> emits.</param>
    /// <param name="cancellationToken">
    ///     Stops the walk. Observed before the first row is read and at least every 4096 rows
    ///     thereafter, so a cancelled query leaves the mapping promptly.
    /// </param>
    /// <returns>The entry matching the path, or null when no entry matches.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public FileEntry? Find(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var query = BeginQuery(cancellationToken, current => LookupEngine.SelectPathBlock(current, path));
        return LookupEngine.Find(query.DriveBlock, path, query.CancellationToken);
    }

    /// <summary>
    ///     The whole match set, materialized. Callers page by slicing the returned list, which is
    ///     why the count is available up front and there is no cursor.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     128 parent hops while applying the subtree restriction
    ///     (<see cref="SearchQuery.Subtree" />).
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="query" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="query" /> has an undefined <see cref="SearchQuery.MatchMode" />.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ArgumentException">
    ///     <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />, belongs to another
    ///     <see cref="FileIndex" /> or to a drive block a rescan replaced, or has been superseded by a newer
    ///     file reusing its row slot. The default value is rejected before the index is touched; the others are
    ///     judged against the block this query borrowed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed, or the subtree anchor's own index has.</exception>
    /// <seealso cref="Enumerate" />
    public IReadOnlyList<FileEntry> Search(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        SubtreeAnchor.ThrowIfDefault(query);
        using var scope = BeginQuery(cancellationToken, current => SubtreeAnchor.SelectBlock(current, query));
        return SearchEngine.Search(scope.DriveBlock, query, scope.CancellationToken);
    }

    /// <summary>
    ///     The streaming form of <see cref="Search" />: the same matches in the same order,
    ///     yielded one at a time instead of materialized into a list, for a consumer that
    ///     filters most rows away and should not pay memory for the whole match set. This is
    ///     the consumer escape hatch over the mapped rows; the row scanner itself stays internal.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <returns>
    ///     A cold enumerable: the call itself reads nothing and takes no borrow. Each
    ///     enumeration borrows the block it reads from the first row until the enumerator
    ///     completes or is disposed, which a <c>foreach</c> does even on an early
    ///     <c>break</c>. An enumerator abandoned without disposal holds its borrow until it
    ///     is collected, and <see cref="DisposeAsync" /> waits for it like any other borrow.
    ///     Entries stay readable after the enumeration ends, exactly as entries from
    ///     <see cref="Search" /> do.
    /// </returns>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     128 parent hops while applying the subtree
    ///     restriction (<see cref="SearchQuery.Subtree" />). Where <see cref="Search" /> throws
    ///     it before returning, the streaming form throws it from the MoveNext that reaches
    ///     the candidate.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="query" /> has an undefined <see cref="SearchQuery.MatchMode" />.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed, or, from the first MoveNext, the subtree anchor's own index has.</exception>
    /// <exception cref="ArgumentNullException">At call time: <paramref name="query" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     At call time: <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />. From the
    ///     first MoveNext, which takes the borrow the anchor is validated against: the anchor belongs to another
    ///     <see cref="FileIndex" /> or to a drive block a rescan replaced, or has been superseded by a newer file
    ///     reusing its row slot.
    /// </exception>
    /// <seealso cref="Search" />
    public IEnumerable<FileEntry> Enumerate(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        NameMatching.ThrowIfUndefined(query);
        SubtreeAnchor.ThrowIfDefault(query);
        ThrowIfDisposed();
        return EnumerateCore(query, cancellationToken);
    }

    /// <summary>
    ///     Enumerates the rows matching <paramref name="query" /> as non-allocating <see cref="IndexRow" /> views:
    ///     the name is a span over the mapped name pool, and no <see cref="FileEntry" />, name string or path
    ///     is built unless the caller asks a row for one. Consume the result with <c>foreach</c>.
    /// </summary>
    /// <remarks>
    ///     Each GetEnumerator takes its own block borrow; <c>foreach</c> releases it on every exit.
    ///     The borrow keeps that mapping owned and mapped, not its row contents frozen. Journal
    ///     updates write rows in place during a live watch, so predicates and subsequent property reads can
    ///     observe different updates. Name text is append-only; a captured name span keeps its text, but other
    ///     row fields are live reads and do not form an atomic record with it. Separate enumerations can also
    ///     see different mappings. The result, enumerator and rows are ref structs: they cannot be held across
    ///     an <c>await</c>, captured by a lambda or stored in a class or ordinary struct field.
    ///     Dispose a manually obtained enumerator even when scanning throws. Enumerator copies share one
    ///     borrow: disposing any copy invalidates Current and MoveNext on all copies, which then throw
    ///     <see cref="ObjectDisposedException" /> naming IndexRowEnumerator. Repeated disposal is harmless.
    ///     A reachable undisposed enumerator can keep DisposeAsync waiting indefinitely; an unreachable
    ///     borrow can eventually be returned by its internal finalizer, with no timing guarantee.
    /// </remarks>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Observed before the first row and then at least every 4096 rows.</param>
    /// <exception cref="ArgumentNullException">At call time: <paramref name="query" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">At call time: <paramref name="query" /> has an undefined <see cref="SearchQuery.MatchMode" />.</exception>
    /// <exception cref="ArgumentException">
    ///     At call time: <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />. At
    ///     GetEnumerator, which takes the borrow the anchor is validated against: the anchor belongs to another
    ///     <see cref="FileIndex" /> or to a drive block a rescan replaced, or has been superseded by a newer file
    ///     reusing its row slot. The borrow is returned when GetEnumerator throws.
    /// </exception>
    /// <exception cref="ObjectDisposedException">At GetEnumerator: the index has been disposed, or the subtree anchor's own index has. At Current or MoveNext: this enumeration's shared borrow has been returned.</exception>
    /// <exception cref="OperationCanceledException">At GetEnumerator or MoveNext: caller cancellation or index disposal cancels the enumeration.</exception>
    /// <exception cref="InvalidDataException">At MoveNext: a candidate's parent chain does not resolve within 128 parent hops while applying <see cref="SearchQuery.Subtree" />.</exception>
    public IndexRowEnumerable EnumerateRows(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        NameMatching.ThrowIfUndefined(query);
        SubtreeAnchor.ThrowIfDefault(query);
        return new IndexRowEnumerable(this, query, cancellationToken);
    }

    internal IndexRowEnumerator BeginRowEnumeration(SearchQuery query, CancellationToken cancellationToken)
    {
        return new IndexRowEnumerator(
            BeginQuery(cancellationToken, current => SubtreeAnchor.SelectBlock(current, query)), query);
    }

    /// <summary>
    ///     The iterator behind <see cref="Enumerate" />, split out so argument and disposal
    ///     validation throw at call time while the borrow, like the scan, waits for the first
    ///     MoveNext. The scope sits in a using inside the iterator, so the borrow and the
    ///     linked token source are returned when the enumerator completes, is disposed, or
    ///     faults.
    /// </summary>
    IEnumerable<FileEntry> EnumerateCore(SearchQuery query, CancellationToken cancellationToken)
    {
        using var scope = BeginQuery(cancellationToken, current => SubtreeAnchor.SelectBlock(current, query));
        foreach (var entry in SearchEngine.Enumerate(scope.DriveBlock, query, scope.CancellationToken))
        {
            yield return entry;
        }
    }

    /// <summary>
    ///     The entry for the root block's root row, which is the indexed root directory rather
    ///     than the volume root: a block indexed from a subdirectory roots there.
    /// </summary>
    /// <param name="cancellationToken">Stops the lookup before it resolves the root row.</param>
    /// <exception cref="InvalidOperationException">
    ///     The root is offline or failed without a block.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public FileEntry Root(CancellationToken cancellationToken = default)
    {
        using var query = BeginQuery(cancellationToken);
        query.CancellationToken.ThrowIfCancellationRequested();
        if (query.DriveBlock is null)
        {
            Hooks.DrivesReadBeforeLock?.Invoke();
            string message;
            lock (_stateLock)
            {
                message = $"Drive {_runtime.Drive} has no block (state: {_runtime.BlocklessState}).";
            }

            throw new InvalidOperationException(message);
        }

        return LookupEngine.Root(query.DriveBlock, query.CancellationToken);
    }

    /// <summary>
    ///     Takes a reader's claim on the current block. Taken under <see cref="_stateLock" />,
    ///     the same lock a block swap and index disposal publish through, so a borrow is
    ///     either counted before the block leaves the index (and waited for when it is
    ///     released) or refused because the release has already begun.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    internal SnapshotBorrow BorrowCurrentSnapshot() => BorrowBlock(null);

    /// <summary>
    ///     The borrow behind <see cref="BorrowCurrentSnapshot" />: <paramref name="select" /> picks whether
    ///     to hold the current block under the state lock, and null holds it unconditionally.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    SnapshotBorrow BorrowBlock(Func<DriveBlock?, DriveBlock?>? select)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _runtime.Block;
            return new SnapshotBorrow(select is null ? current : select(current));
        }
    }

    /// <summary>
    ///     Everything a query needs and nothing it has to remember to give back by hand: the
    ///     block to read, the token to observe, and a borrow that is returned when the query
    ///     returns, whether it answered or threw.
    /// </summary>
    internal QueryScope BeginQuery(CancellationToken cancellationToken,
        Func<DriveBlock?, DriveBlock?>? select = null)
    {
        var scope = AdmitQuery(cancellationToken, select);
        Hooks.ReaderAdmitted?.Invoke();
        return scope;
    }

    /// <summary>
    ///     Builds the scope <see cref="BeginQuery" /> hands out: the caller's token is checked,
    ///     linked to disposal when it can be cancelled at all, and the borrow is taken last.
    /// </summary>
    QueryScope AdmitQuery(CancellationToken cancellationToken,
        Func<DriveBlock?, DriveBlock?>? select = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled)
        {
            return new QueryScope(BorrowBlock(select), DisposalToken);
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, DisposalToken);
        var handedOver = false;
        try
        {
            var scope = new QueryScope(BorrowBlock(select), linkedCancellation);
            handedOver = true;
            return scope;
        }
        finally
        {
            if (!handedOver)
            {
                linkedCancellation.Dispose();
            }
        }
    }

    /// <summary>One query's hold on the index: the borrowed block and the token that stops it.</summary>
    internal readonly struct QueryScope : IDisposable
    {
        readonly SnapshotBorrow _borrow;
        readonly CancellationTokenSource? _linkedCancellation;

        /// <summary>For a query that observes a token it does not own, and so has none to dispose.</summary>
        internal QueryScope(SnapshotBorrow borrow, CancellationToken cancellationToken)
        {
            _borrow = borrow;
            _linkedCancellation = null;
            CancellationToken = cancellationToken;
        }

        /// <summary>For a query that owns the linked source its token comes from.</summary>
        internal QueryScope(SnapshotBorrow borrow, CancellationTokenSource linkedCancellation)
            : this(borrow, linkedCancellation.Token)
        {
            _linkedCancellation = linkedCancellation;
        }

        internal DriveBlock? DriveBlock => _borrow.DriveBlock;

        internal bool IsDisposed => _borrow.IsReturned;

        /// <summary>
        ///     The token the engines observe: cancelled by the caller's own token or by the
        ///     index's disposal, whichever comes first.
        /// </summary>
        internal CancellationToken CancellationToken { get; }

        public void Dispose()
        {
            // The borrow goes back last. Its return is what lets a waiting disposal unmap, so
            // nothing this query still owns may outlive it.
            _linkedCancellation?.Dispose();
            _borrow.Dispose();
        }
    }
}
