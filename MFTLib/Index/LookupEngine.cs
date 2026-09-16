namespace MFTLib.Index;

/// <summary>
///     Point lookups. <see cref="Find" /> resolves a native path by matching the longest indexed
///     root, then walks one name per level. <see cref="FindByName" /> is an exact-name column
///     scan across every current drive block, partitioned per block the way
///     <see cref="SearchEngine" /> partitions its search.
/// </summary>
internal static class LookupEngine
{
    internal static FileEntry Root(Snapshot snapshot, char driveLetter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.FindDriveBlock(driveLetter) is not { } driveBlock)
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        return FileEntry.Create(snapshot, driveBlock.DriveOrdinal, driveBlock.Block.Header.RootRow);
    }

    /// <summary>
    ///     Resolves a native filesystem path against the indexed roots. The block whose root
    ///     directory is the longest prefix of the path wins, so nested indexed roots resolve to
    ///     the inner one, and the remaining segments are walked down from that block's root row.
    /// </summary>
    internal static FileEntry? Find(Snapshot snapshot, string nativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nativePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (FindLongestMatchingRoot(snapshot, nativePath) is not { } match)
        {
            return null;
        }

        var (driveBlock, rootLength) = match;
        var remainder = nativePath.AsSpan(rootLength);
        var currentRow = driveBlock.Block.Header.RootRow;
        var descent = new PathDescent(snapshot, driveBlock.DriveOrdinal, IsCaseSensitive(driveBlock),
            cancellationToken);
        foreach (var segmentRange in remainder.SplitAny(
                     OperatingSystem.IsWindows() ? ['\\', '/'] : ['/']))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = remainder[segmentRange];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (!TryFindChild(in descent, currentRow, segment, out currentRow))
            {
                return null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FileEntry.Create(snapshot, driveBlock.DriveOrdinal, currentRow);
    }

    /// <summary>
    ///     How this block's names compare. An MFT block describes an NTFS volume, which folds
    ///     case, so it is case-insensitive wherever the process runs. An enumeration block
    ///     inherits the host's rule, which is the same decision
    ///     <c>FileIndex.TryOpenExistingBlock</c> already makes when it compares a cached root.
    /// </summary>
    internal static bool IsCaseSensitive(DriveBlock driveBlock)
    {
        ArgumentNullException.ThrowIfNull(driveBlock);
        return driveBlock.ProducerKind == ProducerKind.Enumeration && !OperatingSystem.IsWindows();
    }

    /// <summary>
    ///     The indexed root that is the longest prefix of <paramref name="nativePath" />, with the
    ///     offset where the path below it begins. Windows separators compare equally throughout the prefix.
    ///     A trailing separator on either side is not significant, so
    ///     a root of <c>T:\</c> matches <c>T:\Documents</c> and a root of <c>/data/files</c>
    ///     matches <c>/data/files/</c>. A block with no root directory can never match. The
    ///     comparison form of each root is <see cref="DriveBlock.MatchableRootDirectoryPath" />,
    ///     computed once on the block rather than rebuilt for every candidate on every lookup.
    /// </summary>
    static (DriveBlock DriveBlock, int RootLength)? FindLongestMatchingRoot(
        Snapshot snapshot, string nativePath)
    {
        if (nativePath.Length == 0)
        {
            return null;
        }

        var normalizedPath = OperatingSystem.IsWindows() ? nativePath.Replace('\\', '/') : nativePath;
        DriveBlock? best = null;
        var bestLength = -1;
        foreach (var candidate in snapshot.DriveBlocks)
        {
            if (candidate.MatchableRootDirectoryPath is not { } trimmed)
            {
                continue;
            }

            if (trimmed.Length > nativePath.Length ||
                !normalizedPath.AsSpan(0, trimmed.Length).Equals(trimmed, Comparison(candidate)))
            {
                continue;
            }

            if (nativePath.Length > trimmed.Length &&
                !IsSeparator(nativePath[trimmed.Length]))
            {
                continue;
            }

            if (trimmed.Length > bestLength)
            {
                best = candidate;
                bestLength = trimmed.Length;
            }
        }

        return best is null ? null : (best, bestLength);
    }

    static StringComparison Comparison(DriveBlock driveBlock)
    {
        return IsCaseSensitive(driveBlock) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    static bool IsSeparator(char character)
    {
        return character == '/' || (OperatingSystem.IsWindows() && character == '\\');
    }

    /// <summary>
    ///     What stays the same for every level of one path descent: the block being walked, how
    ///     its names compare, and the token that stops the walk. Passed as one value so each
    ///     level's call carries only what actually changes between levels.
    /// </summary>
    readonly record struct PathDescent(
        Snapshot Snapshot,
        ushort DriveOrdinal,
        bool CaseSensitive,
        CancellationToken CancellationToken);

    static bool TryFindChild(in PathDescent descent, uint parentRow, ReadOnlySpan<char> segment,
        out uint childRow)
    {
        var scanner = new RowScanner(descent.Snapshot, descent.DriveOrdinal, descent.CancellationToken);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == parentRow &&
                scanner.CurrentRowIndex != parentRow &&
                NameMatching.EqualsName(scanner.CurrentName, segment, descent.CaseSensitive))
            {
                childRow = scanner.CurrentRowIndex;
                return true;
            }
        }

        childRow = 0;
        return false;
    }

    internal static List<FileEntry> FindByName(Snapshot snapshot, string name, bool caseSensitive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<FileEntry>();
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindByNameOneDrive(snapshot, driveBlock.DriveOrdinal, name, caseSensitive, results,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     One drive's half of the scan, partitioned the way <see cref="SearchEngine" />
    ///     partitions its search: per-partition result lists merged in partition order, so the
    ///     merged list holds the drive's matches in ascending row order exactly as a sequential
    ///     scan did.
    /// </summary>
    static void FindByNameOneDrive(Snapshot snapshot, ushort driveOrdinal, string name,
        bool caseSensitive, List<FileEntry> results, CancellationToken cancellationToken)
    {
        var rowCount = snapshot.GetDriveBlock(driveOrdinal).Block.Header.RowCount;
        var partitions = ScanPartitioning.Partition(rowCount,
            ScanPartitioning.DefaultPartitionCount(rowCount));
        if (partitions.Count == 0)
        {
            return;
        }

        if (partitions.Count == 1)
        {
            CollectPartition(snapshot, driveOrdinal, name, caseSensitive, partitions[0], results,
                cancellationToken);
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
            CollectPartition(snapshot, driveOrdinal, name, caseSensitive, partitions[index], local,
                cancellationToken);
            perPartition[index] = local;
        });

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var local in perPartition)
        {
            results.AddRange(local);
        }
    }

    static void CollectPartition(Snapshot snapshot, ushort driveOrdinal, string name,
        bool caseSensitive, (uint StartRow, uint EndRowExclusive) partition,
        List<FileEntry> destination, CancellationToken cancellationToken)
    {
        var scanner = new RowScanner(snapshot, driveOrdinal, partition.StartRow,
            partition.EndRowExclusive, cancellationToken);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (row.IsInUse && !row.IsDeleted &&
                NameMatching.EqualsName(scanner.CurrentName, name, caseSensitive))
            {
                destination.Add(FileEntry.Create(snapshot, driveOrdinal, scanner.CurrentRowIndex));
            }
        }
    }
}
