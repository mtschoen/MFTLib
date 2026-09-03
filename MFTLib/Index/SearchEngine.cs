using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The parallel column scan behind every name search. There are no secondary indexes in v1,
///     so a search reads the name, flags, size, and modified columns of every live row on every
///     current drive block and materializes the whole match set. Callers page by slicing.
/// </summary>
internal static class SearchEngine
{
    internal static List<FileEntry> Search(Snapshot snapshot, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<FileEntry>();
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            if (query.Under is { } under && under.DriveOrdinal != driveBlock.DriveOrdinal)
            {
                continue;
            }

            SearchOneDrive(snapshot, driveBlock.DriveOrdinal, query, results);
        }

        return results;
    }

    /// <summary>
    ///     Applies the cheap column predicates first and the subtree walk last, because
    ///     <see cref="IndexNavigation.IsUnder" /> climbs the parent column for every candidate.
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

    static void SearchOneDrive(Snapshot snapshot, ushort driveOrdinal, SearchQuery query, List<FileEntry> results)
    {
        var rowCount = snapshot.GetDriveBlock(driveOrdinal).Block.Header.RowCount;
        var partitions = ScanPartitioning.Partition(rowCount, ScanPartitioning.DefaultPartitionCount(rowCount));
        if (partitions.Count == 0)
        {
            return;
        }

        if (partitions.Count == 1)
        {
            CollectPartition(snapshot, driveOrdinal, query, partitions[0], results);
            return;
        }

        var perPartition = new List<FileEntry>[partitions.Count];
        Parallel.For(0, partitions.Count, index =>
        {
            var local = new List<FileEntry>();
            CollectPartition(snapshot, driveOrdinal, query, partitions[index], local);
            perPartition[index] = local;
        });

        foreach (var local in perPartition)
        {
            results.AddRange(local);
        }
    }

    static void CollectPartition(Snapshot snapshot, ushort driveOrdinal, SearchQuery query,
        (uint StartRow, uint EndRowExclusive) partition, List<FileEntry> destination)
    {
        var candidates = new List<uint>();
        var scanner = new RowScanner(snapshot, driveOrdinal, partition.StartRow, partition.EndRowExclusive);
        while (scanner.MoveNext())
        {
            if (RowMatches(in scanner.Current, scanner.CurrentName, query))
            {
                candidates.Add(scanner.CurrentRowIndex);
            }
        }

        foreach (var rowIndex in candidates)
        {
            var entry = FileEntry.Create(snapshot, driveOrdinal, rowIndex);
            if (query.Under is not { } under || IndexNavigation.IsUnder(entry, under))
            {
                destination.Add(entry);
            }
        }
    }
}
