namespace MFTLib.Index;

/// <summary>
///     Point lookups. <see cref="Find" /> resolves a native path against the indexed root,
///     then walks one name per level.
/// </summary>
static class LookupEngine
{
    internal static FileEntry Root(RootBlock rootBlock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootBlock);
        cancellationToken.ThrowIfCancellationRequested();
        return FileEntry.Create(rootBlock, rootBlock.Block.Header.RootRow);
    }

    /// <summary>The block <see cref="Find" /> reads for this path, or null when the root does not match.</summary>
    internal static RootBlock? SelectPathBlock(RootBlock? current, string nativePath)
    {
        if (current is null)
        {
            return null;
        }

        string? foldedPath = null;
        return TryMatchRootPrefix(current.Kind, current.MatchableRootDirectoryPath, nativePath, ref foldedPath, out _)
            ? current
            : null;
    }

    /// <summary>
    ///     Resolves a native filesystem path against the borrowed block. When the block's root
    ///     directory is a prefix of the path, the remaining segments are walked down from that
    ///     block's root row.
    /// </summary>
    internal static FileEntry? Find(RootBlock? rootBlock, string nativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (rootBlock is null)
        {
            return null;
        }

        string? foldedPath = null;
        if (!TryMatchRootPrefix(rootBlock.Kind, rootBlock.MatchableRootDirectoryPath, nativePath, ref foldedPath,
                out var rootLength))
        {
            return null;
        }

        var remainder = nativePath.AsSpan(rootLength);
        var currentRow = rootBlock.Block.Header.RootRow;
        var descent = new PathDescent(rootBlock, IsCaseSensitive(rootBlock), cancellationToken);
        foreach (var segmentRange in remainder.SplitAny(rootBlock.Kind.Separators))
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
        return FileEntry.Create(rootBlock, currentRow);
    }

    /// <summary>
    ///     How this block's names compare. An MFT block describes an NTFS volume, which folds
    ///     case, so it is case-insensitive wherever the process runs. An enumeration block
    ///     inherits the host's rule, which is the same decision
    ///     <c>FileIndex.TryOpenExistingBlock</c> already makes when it compares a cached root.
    /// </summary>
    static bool IsCaseSensitive(RootBlock rootBlock)
    {
        ArgumentNullException.ThrowIfNull(rootBlock);
        return rootBlock.Kind.IsCaseSensitive;
    }

    /// <summary>
    ///     Checks whether <paramref name="matchableRootDirectoryPath" /> is a valid prefix of
    ///     <paramref name="nativePath" />, returning the offset where the path below the root begins.
    ///     Windows separators compare equally throughout the prefix. A trailing separator on either
    ///     side is not significant, so a root of <c>T:\</c> matches <c>T:\Documents</c> and a root
    ///     of <c>/data/files</c> matches <c>/data/files/</c>.
    /// </summary>
    internal static bool TryMatchRootPrefix(RootKind kind, string? matchableRootDirectoryPath,
        string nativePath, ref string? foldedPath, out int rootLength)
    {
        if (nativePath.Length == 0 || matchableRootDirectoryPath is not { } trimmed)
        {
            rootLength = 0;
            return false;
        }

        var comparable = kind.ComparablePath(nativePath, ref foldedPath);
        if (trimmed.Length > nativePath.Length ||
            !comparable.AsSpan(0, trimmed.Length).Equals(trimmed, kind.Comparison))
        {
            rootLength = 0;
            return false;
        }

        if (nativePath.Length > trimmed.Length &&
            !kind.IsSeparator(nativePath[trimmed.Length]))
        {
            rootLength = 0;
            return false;
        }

        rootLength = trimmed.Length;
        return true;
    }

    /// <summary>
    ///     What stays the same for every level of one path descent: the block being walked, how
    ///     its names compare, and the token that stops the walk. Passed as one value so each
    ///     level's call carries only what actually changes between levels.
    /// </summary>
    readonly record struct PathDescent(
        RootBlock RootBlock,
        bool CaseSensitive,
        CancellationToken CancellationToken);

    /// <summary>
    ///     Finds the child of <paramref name="parentRow" /> named <paramref name="segment" />. The segment resolves to
    ///     the entries that match under the block's case rule, then rows are tested by name identifier. Among several
    ///     ignore-case matches a child spelled exactly as the segment wins; otherwise the lowest row does.
    /// </summary>
    static bool TryFindChild(in PathDescent descent, uint parentRow, ReadOnlySpan<char> segment,
        out uint childRow)
    {
        var query = new SearchQuery(segment.ToString(), NameMatchMode.Exact, descent.CaseSensitive);
        using var names = new NameQueryMatches(descent.RootBlock.Block, query, descent.CancellationToken);
        var found = false;
        childRow = 0;
        var scanner = new RowScanner(descent.RootBlock, descent.CancellationToken);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (!row.IsInUse || row.IsDeleted || row.ParentRow != parentRow ||
                scanner.CurrentRowIndex == parentRow || !names.Matches(in row))
            {
                continue;
            }

            if (names.IsOrdinalMatch(in row))
            {
                childRow = scanner.CurrentRowIndex;
                return true;
            }

            if (!found)
            {
                found = true;
                childRow = scanner.CurrentRowIndex;
            }

            if (!names.HasOrdinalMatch)
            {
                return true;
            }
        }

        return found;
    }
}
