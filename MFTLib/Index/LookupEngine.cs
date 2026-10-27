namespace MFTLib.Index;

/// <summary>
///     Point lookups. <see cref="Find" /> resolves a native path by matching the longest indexed
///     root, then walks one name per level.
/// </summary>
static class LookupEngine
{
    internal static FileEntry Root(IReadOnlyList<DriveBlock> snapshot, RootKey drive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (FindDriveBlock(snapshot, drive) is not { } driveBlock)
        {
            throw new ArgumentException($"Drive {drive} is not part of this index.", nameof(drive));
        }

        return FileEntry.Create(driveBlock, driveBlock.Block.Header.RootRow);
    }

    /// <summary>The one block <see cref="Find" /> reads for this path, or none when no indexed root matches.</summary>
    internal static IReadOnlyList<DriveBlock> SelectPathBlock(IReadOnlyList<DriveBlock> snapshot, string nativePath) =>
        FindLongestMatchingRoot(snapshot, nativePath) is { } match ? [match.DriveBlock] : [];

    /// <summary>The one block <see cref="Root" /> reads for this drive, or none when it has no block.</summary>
    internal static IReadOnlyList<DriveBlock> SelectDriveBlock(IReadOnlyList<DriveBlock> snapshot, RootKey drive) =>
        FindDriveBlock(snapshot, drive) is { } driveBlock ? [driveBlock] : [];

    /// <summary>The snapshot's block for this drive, or null when it has none.</summary>
    internal static DriveBlock? FindDriveBlock(IReadOnlyList<DriveBlock> snapshot, RootKey drive) =>
        snapshot.FirstOrDefault(candidate => candidate.Drive == drive.Validated());

    /// <summary>
    ///     Resolves a native filesystem path against the indexed roots. The block whose root
    ///     directory is the longest prefix of the path wins, so nested indexed roots resolve to
    ///     the inner one, and the remaining segments are walked down from that block's root row.
    /// </summary>
    internal static FileEntry? Find(IReadOnlyList<DriveBlock> snapshot, string nativePath,
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
        var descent = new PathDescent(driveBlock, IsCaseSensitive(driveBlock), cancellationToken);
        foreach (var segmentRange in remainder.SplitAny(driveBlock.Kind.Separators))
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
        return FileEntry.Create(driveBlock, currentRow);
    }

    /// <summary>
    ///     How this block's names compare. An MFT block describes an NTFS volume, which folds
    ///     case, so it is case-insensitive wherever the process runs. An enumeration block
    ///     inherits the host's rule, which is the same decision
    ///     <c>FileIndex.TryOpenExistingBlock</c> already makes when it compares a cached root.
    /// </summary>
    static bool IsCaseSensitive(DriveBlock driveBlock)
    {
        ArgumentNullException.ThrowIfNull(driveBlock);
        return driveBlock.Kind.IsCaseSensitive;
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
        IReadOnlyList<DriveBlock> snapshot, string nativePath)
    {
        if (nativePath.Length == 0)
        {
            return null;
        }

        string? foldedPath = null;
        DriveBlock? best = null;
        var bestLength = -1;
        foreach (var candidate in snapshot)
        {
            if (candidate.MatchableRootDirectoryPath is not { } trimmed)
            {
                continue;
            }

            var comparable = candidate.Kind.ComparablePath(nativePath, ref foldedPath);
            if (trimmed.Length > nativePath.Length ||
                !comparable.AsSpan(0, trimmed.Length).Equals(trimmed, candidate.Kind.Comparison))
            {
                continue;
            }

            if (nativePath.Length > trimmed.Length &&
                !candidate.Kind.IsSeparator(nativePath[trimmed.Length]))
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

    /// <summary>
    ///     What stays the same for every level of one path descent: the block being walked, how
    ///     its names compare, and the token that stops the walk. Passed as one value so each
    ///     level's call carries only what actually changes between levels.
    /// </summary>
    readonly record struct PathDescent(
        DriveBlock DriveBlock,
        bool CaseSensitive,
        CancellationToken CancellationToken);

    static bool TryFindChild(in PathDescent descent, uint parentRow, ReadOnlySpan<char> segment,
        out uint childRow)
    {
        var scanner = new RowScanner(descent.DriveBlock, descent.CancellationToken);
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
}
