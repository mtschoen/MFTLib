namespace MFTLib.Index;

/// <summary>
///     Point lookups. <see cref="Find" /> resolves a native path against the indexed root,
///     then walks one name per level.
/// </summary>
static class LookupEngine
{
    /// <summary>
    ///     The entry for the index's root: the block's root row, or the row a directory view is bound to. The binding is
    ///     read once; the caller holds a block borrow, and a view that is absent is answered by the caller never lending
    ///     the block.
    /// </summary>
    internal static FileEntry Root(PinnedRootBlock pinned, uint? admittedViewRow = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pinned.RootBlock);
        cancellationToken.ThrowIfCancellationRequested();

        // The row admission bound, never a fresh read of a binding a later batch may have cleared.
        return FileEntry.Create(pinned, admittedViewRow ?? pinned.RootBlock.View.Row);
    }

    /// <summary>
    ///     The block <see cref="Find" /> reads for this path, or null when the root does not match. Whether a directory
    ///     view is present is left to admission, which judges it on the mapping the lookup pins.
    /// </summary>
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
    ///     Resolves a native filesystem path against the borrowed block's pinned mapping. When the block's root
    ///     directory is a prefix of the path, the remaining segments are walked down from the row admission bound a
    ///     directory view to, or from the block's root row, never from a fresh read of the shared binding.
    /// </summary>
    internal static FileEntry? Find(PinnedRootBlock? pinned, string nativePath, uint? admittedViewRow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (pinned is not { } root)
        {
            return null;
        }

        var rootBlock = root.RootBlock;

        string? foldedPath = null;
        if (!TryMatchRootPrefix(rootBlock.Kind, rootBlock.MatchableRootDirectoryPath, nativePath, ref foldedPath,
                out var rootLength))
        {
            return null;
        }

        if (rootBlock.View.IsDirectoryView && admittedViewRow is null)
        {
            throw new InvalidOperationException("A directory view must be admitted with its bound row before a lookup.");
        }

        if (!TryDescend(root, admittedViewRow ?? rootBlock.View.Row, nativePath.AsSpan(rootLength), cancellationToken,
                out var foundRow))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FileEntry.Create(root, foundRow);
    }

    /// <summary>
    ///     Walks <paramref name="relativePath" />, a path below the row <paramref name="startRow" />, one name per
    ///     level. False when a level has no live child of that name.
    /// </summary>
    internal static bool TryDescend(PinnedRootBlock pinned, uint startRow, ReadOnlySpan<char> relativePath,
        CancellationToken cancellationToken, out uint row) =>
        TryDescend(pinned, startRow, relativePath, null, cancellationToken, out row);

    /// <summary>
    ///     <see cref="TryDescend(PinnedRootBlock, uint, ReadOnlySpan{char}, CancellationToken, out uint)" /> that also reports how
    ///     far the walk got: every level it tried is added to <paramref name="trace" />, and when a level has no live
    ///     child, <paramref name="row" /> is the last row found and the trace's last level names what was missing.
    /// </summary>
    internal static bool TryDescend(PinnedRootBlock pinned, uint startRow, ReadOnlySpan<char> relativePath,
        DescentTrace? trace, CancellationToken cancellationToken, out uint row)
    {
        row = startRow;
        var descent = new PathDescent(pinned.Block, IsCaseSensitive(pinned.RootBlock), cancellationToken);
        foreach (var segmentRange in relativePath.SplitAny(pinned.RootBlock.Kind.Separators))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = relativePath[segmentRange];
            if (segment.IsEmpty)
            {
                continue;
            }

            var parent = row;
            // Only a directory has children, the rule the parent walk applies, so no path resolves here that
            // FileEntry.Path would not render.
            if (!IndexNavigation.IsParentRow(in descent.Block.Rows[(int)parent]) ||
                !TryFindChild(in descent, parent, segment, out row))
            {
                row = parent;
                trace?.Levels.Add(new DescentLevel(parent, segment.ToString(), null));
                return false;
            }

            trace?.Levels.Add(new DescentLevel(parent, segment.ToString(), row));
        }

        return true;
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

    /// <summary>One level of a path descent: the row searched, the name looked for, and the child found (null when none).</summary>
    internal readonly record struct DescentLevel(uint ParentRow, string Name, uint? FoundRow);

    /// <summary>Where a path descent stood: every level it tried, in order. The last level has no child when the descent failed.</summary>
    internal sealed class DescentTrace
    {
        internal List<DescentLevel> Levels { get; } = [];
    }

    /// <summary>
    ///     What stays the same for every level of one path descent: the pinned mapping being walked, how
    ///     its names compare, and the token that stops the walk. Passed as one value so each
    ///     level's call carries only what actually changes between levels.
    /// </summary>
    readonly record struct PathDescent(
        BlockFile Block,
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
        var block = descent.Block;
        var query = new SearchQuery(segment.ToString(), NameMatchMode.Exact, descent.CaseSensitive);
        using var names = new NameQueryMatches(block, query, descent.CancellationToken);
        using var candidates = PostingCandidates.Gather(block, PostingCandidates.IdentifiersOf(names),
            descent.CancellationToken);
        var found = false;
        childRow = 0;
        var rows = block.Rows;
        var checkpoint = candidates.BeginVerification();
        foreach (var rowIndex in candidates.Rows)
        {
            checkpoint.Step();
            if (rowIndex >= candidates.RowCount)
            {
                continue;
            }

            ref readonly var row = ref rows[(int)rowIndex];
            if (!row.IsInUse || row.IsDeleted || row.ParentRow != parentRow ||
                rowIndex == parentRow || !names.Matches(in row))
            {
                continue;
            }

            if (names.IsOrdinalMatch(in row))
            {
                childRow = rowIndex;
                return true;
            }

            if (!found)
            {
                found = true;
                childRow = rowIndex;
            }

            if (!names.HasOrdinalMatch)
            {
                return true;
            }
        }

        return found;
    }
}
