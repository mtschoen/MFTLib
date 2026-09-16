namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Resolves a native filesystem path to its entry. The indexed root directory that is the
    ///     longest prefix of the path selects the block, and the remaining segments are walked
    ///     down from that block's root row, one name per level. This is the inverse of
    ///     <see cref="FileEntry.Path" />: whatever that emits, this accepts.
    /// </summary>
    /// <param name="nativePath">The path to resolve, in the form <see cref="FileEntry.Path" /> emits.</param>
    /// <param name="cancellationToken">
    ///     Stops the walk. Observed before the first row is read and at least every 4096 rows
    ///     thereafter, so a cancelled query leaves the mapping promptly.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public FileEntry? Find(string nativePath, CancellationToken cancellationToken = default)
    {
        using var query = BeginQuery(cancellationToken);
        return LookupEngine.Find(query.Snapshot, nativePath, query.CancellationToken);
    }

    /// <summary>Exact-name matches across every current drive block, folding case the way NTFS does.</summary>
    /// <param name="name">The name to match in full.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public IReadOnlyList<FileEntry> FindByName(string name, CancellationToken cancellationToken = default)
    {
        using var query = BeginQuery(cancellationToken);
        return LookupEngine.FindByName(query.Snapshot, name, caseSensitive: false, query.CancellationToken);
    }

    /// <summary>
    ///     The whole match set, materialized. Callers page by slicing the returned list, which is
    ///     why the count is available up front and there is no cursor.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops while applying the subtree restriction
    ///     (<see cref="SearchQuery.Under" />).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <seealso cref="Enumerate" />
    public IReadOnlyList<FileEntry> Search(SearchQuery query, CancellationToken cancellationToken = default)
    {
        using var scope = BeginQuery(cancellationToken);
        return SearchEngine.Search(scope.Snapshot, query, scope.CancellationToken);
    }

    /// <summary>
    ///     The streaming form of <see cref="Search" />: the same matches in the same order,
    ///     yielded one at a time instead of materialized into a list, for a consumer that
    ///     filters most rows away and should not pay memory for the whole match set. This is
    ///     the consumer escape hatch over the mapped rows; <see cref="RowScanner" /> itself
    ///     stays internal by design.
    /// </summary>
    /// <param name="query">The predicates a row has to satisfy.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <returns>
    ///     A cold enumerable: the call itself reads nothing and takes no borrow. Each
    ///     enumeration borrows the current snapshot from the first row until the enumerator
    ///     completes or is disposed, which a <c>foreach</c> does even on an early
    ///     <c>break</c>. An enumerator abandoned without disposal holds its borrow until it
    ///     is collected, and <see cref="DisposeAsync" /> waits for it like any other borrow.
    ///     Entries stay readable after the enumeration ends, exactly as entries from
    ///     <see cref="Search" /> do.
    /// </returns>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops while applying the subtree
    ///     restriction (<see cref="SearchQuery.Under" />). Where <see cref="Search" /> throws
    ///     it before returning, the streaming form throws it from the MoveNext that reaches
    ///     the candidate.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <seealso cref="Search" />
    public IEnumerable<FileEntry> Enumerate(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnumerateCore(query, cancellationToken);
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
        using var scope = BeginQuery(cancellationToken);
        foreach (var entry in SearchEngine.Enumerate(scope.Snapshot, query, scope.CancellationToken))
        {
            yield return entry;
        }
    }

    /// <summary>
    ///     Returns the largest files across the current snapshot, optionally restricted to an inclusive subtree.
    /// </summary>
    /// <param name="count">How many entries to return at most.</param>
    /// <param name="under">The inclusive subtree to stay within, or null for the whole index.</param>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops while applying the subtree restriction
    ///     (<paramref name="under" />).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public IReadOnlyList<FileEntry> Largest(int count, FileEntry? under = null,
        CancellationToken cancellationToken = default)
    {
        using var query = BeginQuery(cancellationToken);
        return AggregateEngine.Largest(query.Snapshot, count, under, query.CancellationToken);
    }

    /// <summary>
    ///     Groups every live file name that occurs more than once across the current snapshot.
    ///     This is the longest scan the index offers, several passes over the name column, which
    ///     is where a token earns its keep.
    /// </summary>
    /// <param name="cancellationToken">Stops the scan, as described on <see cref="Find" />.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public IReadOnlyList<DuplicateGroup> DuplicateNames(CancellationToken cancellationToken = default)
    {
        using var query = BeginQuery(cancellationToken);
        return AggregateEngine.DuplicateNames(query.Snapshot, query.CancellationToken);
    }

    /// <summary>
    ///     The entry for the drive block's root row, which is the indexed root directory rather
    ///     than the volume root: a block indexed from a subdirectory roots there.
    /// </summary>
    /// <param name="drive">The drive letter, as configured in <see cref="FileIndexOptions.Drives" />.</param>
    /// <param name="cancellationToken">Stops the lookup before it resolves the root row.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    public FileEntry Root(char drive, CancellationToken cancellationToken = default)
    {
        using var query = BeginQuery(cancellationToken);
        return LookupEngine.Root(query.Snapshot, drive, query.CancellationToken);
    }

    /// <summary>
    ///     The ref-struct escape hatch over one drive's mapped rows, for hot paths that cannot
    ///     afford a materialized list. Internal by design (MFTLib#122): <see cref="Enumerate" />
    ///     is the public streaming surface. The scanner outlives this call, so the caller passes
    ///     in the borrow that keeps those rows mapped for as long as it reads them, rather than
    ///     this method taking one it cannot hold.
    /// </summary>
    internal RowScanner Scan(SnapshotBorrow borrow, ushort driveOrdinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new RowScanner(borrow.Snapshot, driveOrdinal, cancellationToken);
    }

    /// <summary>
    ///     Takes a reader's claim on the current snapshot. Taken under <see cref="_stateLock" />,
    ///     the same lock a snapshot swap and index disposal publish through, so a borrow is
    ///     either counted before the snapshot leaves the index (and waited for when it is
    ///     released) or refused because the release has already begun.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    internal SnapshotBorrow BorrowCurrentSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            var snapshot = _snapshot ?? throw new ObjectDisposedException(nameof(FileIndex));
            return snapshot.Borrow();
        }
    }

    /// <summary>
    ///     Everything a query needs and nothing it has to remember to give back by hand: the
    ///     snapshot to read, the token to observe, and a borrow that is returned when the query
    ///     returns, whether it answered or threw.
    /// </summary>
    QueryScope BeginQuery(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled)
        {
            // Nothing to link: disposal is the whole of this query's cancellation. Linking anyway
            // would allocate a source and register on the one source every query shares, whose
            // registration takes that source's lock, so a consumer querying in a tight loop would
            // serialize its threads through it.
            return new QueryScope(BorrowCurrentSnapshot(), DisposalToken);
        }

        // Linked per query rather than shared, so the caller's own cancellation and the index's
        // disposal both stop this scan and neither can stop anyone else's. Built before the
        // borrow is taken, and given back if the borrow is refused: a borrow counts from the
        // moment it is taken, so anything that throws between that count and the scope that
        // returns it would leave every later release waiting on a reader that no longer exists.
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, DisposalToken);
        var handedOver = false;
        try
        {
            var scope = new QueryScope(BorrowCurrentSnapshot(), linkedCancellation);
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

    /// <summary>One query's hold on the index: the borrowed snapshot and the token that stops it.</summary>
    readonly struct QueryScope : IDisposable
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

        internal Snapshot Snapshot => _borrow.Snapshot;

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
