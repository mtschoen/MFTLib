using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Resolves matching name identifiers once, then scans row columns in parallel and
///     materializes the whole match set. Callers page by slicing.
/// </summary>
static class SearchEngine
{
    internal static List<FileEntry> Search(RootBlock? rootBlock, SearchQuery query,
        uint? admittedViewRow = null, CancellationToken cancellationToken = default)
    {
        NameMatching.ThrowIfUndefined(query);
        cancellationToken.ThrowIfCancellationRequested();

        var subtree = SubtreeAnchor.Resolve(rootBlock, query);
        var results = new List<FileEntry>();
        if (rootBlock is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var membership = SubtreeAnchor.CreateMembership(rootBlock, subtree, admittedViewRow);
            SearchOneRoot(new RootScan(rootBlock, query, membership), results, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     The streaming half of <see cref="Search" />: the same rows in ascending row order, so the
    ///     entries produced are exactly the ones <see cref="Search" /> would return, without the list.
    ///     Single threaded where <see cref="Search" /> partitions: the caller pulls rows one at a
    ///     time, and the borrow <see cref="FileIndex.Enumerate" /> takes is what keeps the
    ///     block mapped while it does.
    /// </summary>
    internal static IEnumerable<FileEntry> Enumerate(RootBlock? rootBlock, SearchQuery query,
        uint? admittedViewRow = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var subtree = SubtreeAnchor.Resolve(rootBlock, query);
        if (rootBlock is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var membership = SubtreeAnchor.CreateMembership(rootBlock, subtree, admittedViewRow);
            var scan = new RootScan(rootBlock, query, membership);
            foreach (var entry in EnumerateRoot(scan, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    ///     Applies the cheap column predicates first and the subtree walk last, because
    ///     <see cref="SubtreeMembership.Contains" /> climbs the parent column for every candidate it has not decided.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    internal static bool RowMatches(in FileRow row, NameQueryMatches names, SearchQuery query) =>
        RowColumnsMatch(in row, query) && names.Matches(in row);

    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping.")]
    static bool RowColumnsMatch(in FileRow row, SearchQuery query)
    {
        if (!row.IsInUse || (row.IsDeleted && !query.IncludeDeleted))
        {
            return false;
        }

        if (query.IsDirectory is { } wantDirectories && row.IsDirectory != wantDirectories)
        {
            return false;
        }

        if (query.SizeMinimum is { } minimumSize && (!row.SizeKnown || row.Size < minimumSize))
        {
            return false;
        }

        if (query.SizeMaximum is { } maximumSize && (!row.SizeKnown || row.Size > maximumSize))
        {
            return false;
        }

        return MatchesModifiedBounds(in row, query);
    }

    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    static bool MatchesModifiedBounds(in FileRow row, SearchQuery query)
    {
        if (query.LastWriteTimeMinimum is null && query.LastWriteTimeMaximum is null)
        {
            return true;
        }

        // An unknown last-write time holds zero, which would satisfy any upper bound and fail any lower
        // bound by accident, so a bounded query excludes the row outright, as it does for an unknown size.
        if (!row.LastWriteTimeKnown)
        {
            return false;
        }

        if (query.LastWriteTimeMinimum is { } after && row.ModifiedTicks < after.Ticks)
        {
            return false;
        }

        return query.LastWriteTimeMaximum is not { } before || row.ModifiedTicks <= before.Ticks;
    }

    /// <summary>
    ///     One root's share of a query: the borrowed root block, the query, and the subtree membership test the
    ///     scan shares between its partitions (an explicit anchor resolved by <see cref="SubtreeAnchor" />, or the
    ///     directory a view is rooted at). It is null when the query excludes no row.
    /// </summary>
    readonly record struct RootScan(RootBlock RootBlock, SearchQuery Query, SubtreeMembership? Membership);

    static void SearchOneRoot(RootScan scan, List<FileEntry> results, CancellationToken cancellationToken)
    {
        using var names = new NameQueryMatches(scan.RootBlock.Block, scan.Query, cancellationToken);
        var rowCount = scan.RootBlock.Block.Header.RowCount;
        var partitions = ScanPartitioning.Partition(rowCount, ScanPartitioning.DefaultPartitionCount(rowCount));
        if (partitions.Count == 0)
        {
            return;
        }

        if (partitions.Count == 1)
        {
            CollectPartition(scan, names, partitions[0], results, cancellationToken);
            return;
        }

        var perPartition = CollectPartitions(scan, names, partitions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var local in perPartition)
        {
            results.AddRange(local);
        }
    }

    static List<FileEntry>[] CollectPartitions(RootScan scan, NameQueryMatches names,
        List<(uint StartRow, uint EndRowExclusive)> partitions, CancellationToken cancellationToken)
    {
        var perPartition = new List<FileEntry>[partitions.Count];

        // The token goes on the loop as well as into every partition's scanner, so cancellation
        // reaches the caller as one OperationCanceledException for the query rather than an
        // AggregateException holding one per partition for it to unwrap.
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, partitions.Count, options, index =>
        {
            var local = new List<FileEntry>();
            CollectPartition(scan, names, partitions[index], local, cancellationToken);
            perPartition[index] = local;
        });

        return perPartition;
    }

    static void CollectPartition(RootScan scan, NameQueryMatches names, (uint StartRow, uint EndRowExclusive) partition,
        List<FileEntry> destination, CancellationToken cancellationToken)
    {
        var (rootBlock, query, membership) = scan;
        var candidates = new List<uint>();
        var scanner = new RowScanner(rootBlock, partition.StartRow, partition.EndRowExclusive, cancellationToken);
        while (scanner.MoveNext())
        {
            if (RowMatches(in scanner.Current, names, query))
            {
                candidates.Add(scanner.CurrentRowIndex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var checkCadence = 0;

        foreach (var rowIndex in candidates)
        {
            if (membership is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!membership.Contains(rowIndex))
                {
                    continue;
                }
            }
            else if ((checkCadence++ & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            destination.Add(FileEntry.Create(rootBlock, rowIndex));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    ///     One root's matching rows in ascending row order. The cancellation cadence mirrors
    ///     <see cref="RowScanner" />: the first row is a checkpoint, then one every
    ///     <see cref="RowScanner.CancellationCheckIntervalRows" /> rows. A scanner itself
    ///     cannot be used here because an iterator may not hold a ref struct across a
    ///     <c>yield return</c>, so the row and its name are re-read per iteration by
    ///     <see cref="RowMatches(BlockFile, uint, NameQueryMatches, SearchQuery)" /> instead.
    /// </summary>
    static IEnumerable<FileEntry> EnumerateRoot(RootScan scan, CancellationToken cancellationToken)
    {
        var (rootBlock, query, membership) = scan;
        var block = rootBlock.Block;
        using var names = new NameQueryMatches(block, query, cancellationToken);
        var rowCount = block.Header.RowCount;
        var rowsUntilCancellationCheck = 1u;

        for (var rowIndex = 0u; rowIndex < rowCount; rowIndex++)
        {
            rowsUntilCancellationCheck--;
            if (rowsUntilCancellationCheck == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowsUntilCancellationCheck = RowScanner.CancellationCheckIntervalRows;
            }

            if (!RowMatches(block, rowIndex, names, query))
            {
                continue;
            }

            if (membership is not null && !membership.Contains(rowIndex))
            {
                continue;
            }

            yield return FileEntry.Create(rootBlock, rowIndex);
        }
    }

    /// <summary>
    ///     The row read and the name span live only inside this call, so the iterator above
    ///     never holds a span or a ref across a <c>yield return</c>, which the language
    ///     forbids.
    /// </summary>
    static bool RowMatches(BlockFile block, uint rowIndex, NameQueryMatches names, SearchQuery query)
    {
        ref readonly var row = ref block.Rows[(int)rowIndex];
        return RowMatches(in row, names, query);
    }
}
