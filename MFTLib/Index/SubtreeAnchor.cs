namespace MFTLib.Index;

/// <summary>
///     A <see cref="SearchQuery.Subtree" /> anchor resolved against the root block a query borrowed: the
///     anchor's row in that block. Both query engines and the row enumerator read this value and never
///     the handle's private coordinates.
/// </summary>
/// <param name="RowIndex">The anchor's row in the borrowed block.</param>
readonly record struct ResolvedSubtree(uint RowIndex);

/// <summary>
///     The one place a subtree anchor is validated. Another index can use the same row numbers, and a rescan
///     can replace the anchor's block so the same coordinates name a different directory. The anchor is
///     therefore accepted only when its own <see cref="RootBlock" /> is the block the query
///     borrowed and its row slot still holds the incarnation it was minted for, so republishing any other
///     root in a set never rejects it.
/// </summary>
static class SubtreeAnchor
{
    /// <summary>Rejects <c>Subtree = default(FileEntry)</c> eagerly, on call entry, before any borrow is taken.</summary>
    /// <param name="query">The query to check.</param>
    /// <exception cref="ArgumentException">The query's subtree is the default <see cref="FileEntry" />.</exception>
    internal static void ThrowIfDefault(SearchQuery query)
    {
        if (query.Subtree is { IsValid: false })
        {
            throw DefaultAnchor(nameof(query));
        }
    }

    static ArgumentException DefaultAnchor(string parameterName) => new(
        "SearchQuery.Subtree is the default FileEntry, which names no entry. Leave Subtree null for an " +
        "unrestricted query, or take an anchor from the index being queried.", parameterName);

    /// <summary>
    ///     The block a query reads: the current block when the query has no anchor, otherwise the anchor's own block
    ///     when it is current, and null when it is not, which <see cref="Resolve" /> then rejects.
    /// </summary>
    internal static RootBlock? SelectBlock(RootBlock? current, SearchQuery query)
    {
        if (query.Subtree is not { } anchor)
        {
            return current;
        }

        return anchor.NamedRootBlock is { } anchorBlock && ReferenceEquals(current, anchorBlock) ? anchorBlock : null;
    }

    /// <summary>
    ///     Resolves the query's anchor against the block the query borrowed, so a publication that lands
    ///     after admission cannot turn the validation into a check-then-use race.
    /// </summary>
    /// <param name="borrowed">The root block this query holds a borrow on, or null when none matched.</param>
    /// <param name="query">The query whose anchor is resolved.</param>
    /// <returns>The resolved anchor, or null when the query has no subtree restriction.</returns>
    /// <exception cref="ArgumentException">
    ///     The anchor is the default <see cref="FileEntry" />, belongs to another index or to a root block a
    ///     rescan replaced, or has been superseded by a newer file reusing its row slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The anchor's root block has been released.</exception>
    internal static ResolvedSubtree? Resolve(RootBlock? borrowed, SearchQuery query)
    {
        if (query.Subtree is not { } anchor)
        {
            return null;
        }

        if (anchor.NamedRootBlock is not { } anchorBlock)
        {
            throw DefaultAnchor(nameof(query));
        }

        // A borrowed block stays mapped even once a disposal has started releasing it, and that disposal
        // reaches the scan as cancellation. Any other released block has been unmapped.
        if (!ReferenceEquals(borrowed, anchorBlock))
        {
            throw ForeignOrDisposed(anchorBlock, nameof(query));
        }

        if (anchor.IsSupersededWhileMapped)
        {
            throw new ArgumentException(
                "SearchQuery.Subtree names an entry whose row slot a newer file has reused after its deletion. " +
                "Take a new anchor from the index being queried.", nameof(query));
        }

        return new ResolvedSubtree(anchor.RowIndex);
    }

    /// <summary>
    ///     The membership test that restricts a query on <paramref name="rootBlock" /> to a subtree, or null when no row
    ///     is excluded: the explicit anchor when the query has one, intersected with the directory a view is rooted at.
    ///     The caller disposes it when the root's scan ends.
    /// </summary>
    internal static SubtreeMembership? CreateMembership(RootBlock rootBlock, ResolvedSubtree? explicitAnchor,
        uint? admittedViewRow)
    {
        // A directory view is never unrestricted: its admitted row is mandatory, and an explicit anchor narrows it
        // further, so an anchor that has moved out of the view matches nothing.
        if (rootBlock.View.IsDirectoryView && admittedViewRow is null)
        {
            throw new InvalidOperationException("A directory view must be admitted with its bound row before it is scanned.");
        }

        var view = admittedViewRow is { } viewRow && rootBlock.View.IsDirectoryView
            ? new SubtreeMembership(rootBlock.Block, viewRow)
            : null;
        return explicitAnchor is { } anchor
            ? new SubtreeMembership(rootBlock.Block, anchor.RowIndex, view)
            : view;
    }

    internal static Exception ForeignOrDisposed(RootBlock anchorBlock, string parameterName)
    {
        ObjectDisposedException.ThrowIf(anchorBlock.IsReleased, typeof(FileEntry));
        return new ArgumentException(
            "SearchQuery.Subtree does not belong to the queried index's current block for root " +
            $"{anchorBlock.Root}: it came from another index or from a block a rescan replaced. " +
            "Take a new anchor from the index being queried.", parameterName);
    }
}
