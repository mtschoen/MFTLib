namespace MFTLib.Index;

/// <summary>
///     Whole-drive aggregates. <see cref="Largest" /> is a bounded partial sort over the size
///     column: one row-scanner pass per drive applies the cheap filters, then the subtree
///     restriction, then a bounded min-heap, so a 21-million-row drive costs one scan and a
///     small heap rather than a sort of the whole drive. <see cref="DuplicateNames" /> runs a
///     chain of fixed-size hash sieve passes over the name column, narrowing the candidate set
///     before any name string is materialized; see <see cref="DuplicateNameFinder" /> for the
///     chain itself and its memory bound.
/// </summary>
internal static class AggregateEngine
{
    internal static List<FileEntry> Largest(Snapshot snapshot, int count, FileEntry? under)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return [];
        }

        var best = new PriorityQueue<FileEntry, long>(count);
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            if (under is { } ancestor && ancestor.DriveOrdinal != driveBlock.DriveOrdinal)
            {
                continue;
            }

            CollectLargestFromDrive(snapshot, driveBlock.DriveOrdinal, count, under, best);
        }

        var results = new List<FileEntry>(best.Count);
        while (best.TryDequeue(out var entry, out _))
        {
            results.Add(entry);
        }

        results.Reverse();
        return results;
    }

    internal static List<DuplicateGroup> DuplicateNames(Snapshot snapshot)
    {
        return DuplicateNameFinder.Find(snapshot, DuplicateNameSieveOptions.Default, out _);
    }

    /// <summary>
    ///     One scanner pass per drive: the cheap in-use/deleted/directory filter runs first,
    ///     then the subtree restriction, which is the only check expensive enough to be worth
    ///     skipping for rows that already failed the cheap filter. Survivors go straight into the
    ///     bounded heap; nothing is buffered into an intermediate list first.
    /// </summary>
    static void CollectLargestFromDrive(Snapshot snapshot, ushort driveOrdinal, int count,
        FileEntry? under, PriorityQueue<FileEntry, long> best)
    {
        var scanner = new RowScanner(snapshot, driveOrdinal);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (!row.IsInUse || row.IsDeleted || row.IsDirectory || !row.SizeKnown)
            {
                continue;
            }

            var entry = FileEntry.Create(snapshot, driveOrdinal, scanner.CurrentRowIndex);
            if (under is { } ancestor && !IndexNavigation.IsUnder(entry, ancestor))
            {
                continue;
            }

            best.Enqueue(entry, entry.Size);
            if (best.Count > count)
            {
                best.Dequeue();
            }
        }
    }
}
