namespace MFTLib.Index;

/// <summary>
///     Point lookups. <see cref="Find" /> resolves a native path by matching the longest indexed
///     root, then walks one name per level. <see cref="FindByName" /> is an exact-name column
///     scan across every current drive block.
/// </summary>
internal static class LookupEngine
{
    internal static FileEntry Root(Snapshot snapshot, char driveLetter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
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
    internal static FileEntry? Find(Snapshot snapshot, string nativePath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nativePath);

        if (FindLongestMatchingRoot(snapshot, nativePath) is not { } match)
        {
            return null;
        }

        var (driveBlock, rootLength) = match;
        var remainder = nativePath.AsSpan(rootLength);
        var currentRow = driveBlock.Block.Header.RootRow;
        var caseSensitive = IsCaseSensitive(driveBlock);
        foreach (var segmentRange in remainder.SplitAny(
                     OperatingSystem.IsWindows() ? ['\\', '/'] : ['/']))
        {
            var segment = remainder[segmentRange];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (!TryFindChild(snapshot, driveBlock.DriveOrdinal, currentRow, segment, caseSensitive, out currentRow))
            {
                return null;
            }
        }

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

    static bool TryFindChild(Snapshot snapshot, ushort driveOrdinal, uint parentRow,
        ReadOnlySpan<char> segment, bool caseSensitive, out uint childRow)
    {
        var scanner = new RowScanner(snapshot, driveOrdinal);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == parentRow &&
                scanner.CurrentRowIndex != parentRow &&
                NameMatching.EqualsName(scanner.CurrentName, segment, caseSensitive))
            {
                childRow = scanner.CurrentRowIndex;
                return true;
            }
        }

        childRow = 0;
        return false;
    }

    internal static List<FileEntry> FindByName(Snapshot snapshot, string name, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(name);

        var results = new List<FileEntry>();
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            var scanner = new RowScanner(snapshot, driveBlock.DriveOrdinal);
            while (scanner.MoveNext())
            {
                ref readonly var row = ref scanner.Current;
                if (row.IsInUse && !row.IsDeleted &&
                    NameMatching.EqualsName(scanner.CurrentName, name, caseSensitive))
                {
                    results.Add(FileEntry.Create(snapshot, driveBlock.DriveOrdinal, scanner.CurrentRowIndex));
                }
            }
        }

        return results;
    }
}
