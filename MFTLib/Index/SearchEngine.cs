using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The parallel column scan behind every name search. There are no secondary indexes in v1,
///     so a search reads the name, flags, size, and modified columns of every live row on every
///     current drive block and materializes the whole match set. Callers page by slicing.
/// </summary>
internal static class SearchEngine
{
    internal static List<FileEntry> Search(Snapshot snapshot, SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Under is { } underAncestor && !underAncestor.IsValid)
        {
            return [];
        }

        var results = new List<FileEntry>();
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Under is { } under && (!under.IsValid || under.DriveOrdinal != driveBlock.DriveOrdinal))
            {
                continue;
            }

            SearchOneDrive(snapshot, driveBlock.DriveOrdinal, query, results, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     Applies the cheap column predicates first and the subtree walk last, because
    ///     <see cref="IndexNavigation.IsUnder(BlockFile, uint, uint)" /> climbs the parent column for every candidate.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    internal static bool RowMatches(in FileRow row, ReadOnlySpan<char> name, SearchQuery query)
    {
        if (!row.IsInUse || row.IsDeleted)
        {
            return false;
        }

        if (query.Directories is { } wantDirectories && row.IsDirectory != wantDirectories)
        {
            return false;
        }

        if (query.MinimumSize is { } minimumSize && (!row.SizeKnown || row.Size < minimumSize))
        {
            return false;
        }

        if (query.MaximumSize is { } maximumSize && (!row.SizeKnown || row.Size > maximumSize))
        {
            return false;
        }

        if (!MatchesModifiedBounds(in row, query))
        {
            return false;
        }

        return query.NamePattern is null || NameMatching.Matches(name, query.NamePattern, query.CaseSensitive);
    }

    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    static bool MatchesModifiedBounds(in FileRow row, SearchQuery query)
    {
        if (query.ModifiedAfter is { } after && row.ModifiedTicks < after.Ticks)
        {
            return false;
        }

        return query.ModifiedBefore is not { } before || row.ModifiedTicks <= before.Ticks;
    }

    static void SearchOneDrive(Snapshot snapshot, ushort driveOrdinal, SearchQuery query,
        List<FileEntry> results, CancellationToken cancellationToken)
    {
        var rowCount = snapshot.GetDriveBlock(driveOrdinal).Block.Header.RowCount;
        var partitions = ScanPartitioning.Partition(rowCount, ScanPartitioning.DefaultPartitionCount(rowCount));
        if (partitions.Count == 0)
        {
            return;
        }

        if (partitions.Count == 1)
        {
            CollectPartition(snapshot, driveOrdinal, query, partitions[0], results, cancellationToken);
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
            CollectPartition(snapshot, driveOrdinal, query, partitions[index], local, cancellationToken);
            perPartition[index] = local;
        });

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var local in perPartition)
        {
            results.AddRange(local);
        }
    }

    static void CollectPartition(Snapshot snapshot, ushort driveOrdinal, SearchQuery query,
        (uint StartRow, uint EndRowExclusive) partition, List<FileEntry> destination,
        CancellationToken cancellationToken)
    {
        var candidates = new List<uint>();
        var scanner = new RowScanner(snapshot, driveOrdinal, partition.StartRow, partition.EndRowExclusive,
            cancellationToken);
        while (scanner.MoveNext())
        {
            if (RowMatches(in scanner.Current, scanner.CurrentName, query))
            {
                candidates.Add(scanner.CurrentRowIndex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var block = snapshot.GetDriveBlock(driveOrdinal).Block;
        var under = query.Under;
        var underRow = under?.RowIndex ?? 0;
        var checkCadence = 0;

        foreach (var rowIndex in candidates)
        {
            if (under is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!under.Value.IsValid || under.Value.DriveOrdinal != driveOrdinal ||
                    !IndexNavigation.IsUnder(block, rowIndex, underRow))
                {
                    continue;
                }
            }
            else if ((checkCadence++ & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            destination.Add(FileEntry.Create(snapshot, driveOrdinal, rowIndex));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
