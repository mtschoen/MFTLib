using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The parallel column scan behind every name search. There are no secondary indexes in v1,
///     so a search reads the name, flags, size, and modified columns of every live row on the
///     current root block and materializes the whole match set. Callers page by slicing.
/// </summary>
static class SearchEngine
{
    internal static List<FileEntry> Search(RootBlock? rootBlock, SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        NameMatching.ThrowIfUndefined(query);
        cancellationToken.ThrowIfCancellationRequested();

        var subtree = SubtreeAnchor.Resolve(rootBlock, query);
        var results = new List<FileEntry>();
        if (rootBlock is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchOneRoot(new RootScan(rootBlock, query, subtree?.RowIndex), results, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var subtree = SubtreeAnchor.Resolve(rootBlock, query);
        if (rootBlock is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = new RootScan(rootBlock, query, subtree?.RowIndex);
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
    ///     <see cref="IndexNavigation.IsUnder(BlockFile, uint, uint)" /> climbs the parent column for every candidate.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    internal static bool RowMatches(in FileRow row, ReadOnlySpan<char> name, SearchQuery query)
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

        if (!MatchesModifiedBounds(in row, query))
        {
            return false;
        }

        return query.NamePattern is null || NameMatching.Matches(name, query.NamePattern, query.MatchMode, query.CaseSensitive);
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
    ///     One root's share of a query: the borrowed root block, the query, and the row the
    ///     subtree restriction is anchored at, resolved by <see cref="SubtreeAnchor" />. It is null when the
    ///     query has no restriction.
    /// </summary>
    readonly record struct RootScan(RootBlock RootBlock, SearchQuery Query, uint? UnderRow);

    static void SearchOneRoot(RootScan scan, List<FileEntry> results, CancellationToken cancellationToken)
    {
        var rowCount = scan.RootBlock.Block.Header.RowCount;
        var partitions = ScanPartitioning.Partition(rowCount, ScanPartitioning.DefaultPartitionCount(rowCount));
        if (partitions.Count == 0)
        {
            return;
        }

        if (partitions.Count == 1)
        {
            CollectPartition(scan, partitions[0], results, cancellationToken);
            return;
        }

        var perPartition = new List<FileEntry>[partitions.Count];

        // The token goes on the loop as well as into every partition's scanner, so cancellation
        // reaches the caller as one OperationCanceledException for the query rather than an
        // AggregateException holding one per partition for it to unwrap.
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, partitions.Count, options, index =>
        {
            var local = new List<FileEntry>();
            CollectPartition(scan, partitions[index], local, cancellationToken);
            perPartition[index] = local;
        });

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var local in perPartition)
        {
            results.AddRange(local);
        }
    }

    static void CollectPartition(RootScan scan, (uint StartRow, uint EndRowExclusive) partition,
        List<FileEntry> destination, CancellationToken cancellationToken)
    {
        var (rootBlock, query, underRow) = scan;
        var candidates = new List<uint>();
        var scanner = new RowScanner(rootBlock, partition.StartRow, partition.EndRowExclusive, cancellationToken);
        while (scanner.MoveNext())
        {
            if (RowMatches(in scanner.Current, scanner.CurrentName, query))
            {
                candidates.Add(scanner.CurrentRowIndex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var block = rootBlock.Block;
        var checkCadence = 0;

        foreach (var rowIndex in candidates)
        {
            if (underRow is { } anchorRow)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IndexNavigation.IsUnder(block, rowIndex, anchorRow))
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
    ///     <see cref="RowMatches(BlockFile, uint, SearchQuery)" /> instead.
    /// </summary>
    static IEnumerable<FileEntry> EnumerateRoot(RootScan scan, CancellationToken cancellationToken)
    {
        var (rootBlock, query, underRow) = scan;
        var block = rootBlock.Block;
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

            if (!RowMatches(block, rowIndex, query))
            {
                continue;
            }

            if (underRow is { } anchorRow && !IndexNavigation.IsUnder(block, rowIndex, anchorRow))
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
    static bool RowMatches(BlockFile block, uint rowIndex, SearchQuery query)
    {
        ref readonly var row = ref block.Rows[(int)rowIndex];
        return RowMatches(in row, NamePool.ReadRowName(block, rowIndex), query);
    }
}
