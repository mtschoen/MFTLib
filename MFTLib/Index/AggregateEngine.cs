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
    internal static List<FileEntry> Largest(Snapshot snapshot, int count, FileEntry? under,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0 || (under is { } underAncestor && !underAncestor.IsValid))
        {
            return [];
        }

        var best = new PriorityQueue<FileEntry, long>(count);
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (under is { } ancestor && (!ancestor.IsValid || ancestor.DriveOrdinal != driveBlock.DriveOrdinal))
            {
                continue;
            }

            CollectLargestFromDrive(snapshot, driveBlock.DriveOrdinal, count, under, best, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<FileEntry>(best.Count);
        var checkCadence = 0;
        while (best.TryDequeue(out var entry, out _))
        {
            if ((checkCadence++ & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            results.Add(entry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        results.Reverse();
        return results;
    }

    internal static List<DuplicateGroup> DuplicateNames(Snapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return DuplicateNameFinder.Find(snapshot, DuplicateNameSieveOptions.Default, out _, cancellationToken);
    }

    /// <summary>
    ///     One scanner pass per drive: the cheap in-use/deleted/directory filter runs first,
    ///     then the subtree restriction, which is the only check expensive enough to be worth
    ///     skipping for rows that already failed the cheap filter. Survivors go straight into the
    ///     bounded heap; nothing is buffered into an intermediate list first.
    /// </summary>
    static void CollectLargestFromDrive(Snapshot snapshot, ushort driveOrdinal, int count,
        FileEntry? under, PriorityQueue<FileEntry, long> best, CancellationToken cancellationToken)
    {
        var block = snapshot.GetDriveBlock(driveOrdinal).Block;
        var underRow = under?.RowIndex ?? 0;
        var scanner = new RowScanner(snapshot, driveOrdinal, cancellationToken);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (!row.IsInUse || row.IsDeleted || row.IsDirectory || !row.SizeKnown)
            {
                continue;
            }

            if (under is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!under.Value.IsValid || under.Value.DriveOrdinal != driveOrdinal ||
                    !IndexNavigation.IsUnder(block, scanner.CurrentRowIndex, underRow))
                {
                    continue;
                }
            }

            best.Enqueue(FileEntry.Create(snapshot, driveOrdinal, scanner.CurrentRowIndex), row.Size);
            if (best.Count > count)
            {
                best.Dequeue();
            }
        }
    }
}
