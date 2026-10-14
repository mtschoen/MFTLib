namespace MFTLib.Index;

/// <summary>
///     A <see cref="SearchQuery.Subtree" /> anchor resolved against the snapshot a query borrowed: the
///     ordinal of the anchor's drive in that snapshot and the anchor's row. Both query engines and the
///     row enumerator read these two values and never the handle's private coordinates.
/// </summary>
/// <param name="DriveOrdinal">The anchor's drive ordinal in the borrowed snapshot.</param>
/// <param name="RowIndex">The anchor's row in that drive's block.</param>
internal readonly record struct ResolvedSubtree(ushort DriveOrdinal, uint RowIndex);

/// <summary>
///     The one place a subtree anchor is validated. A handle's drive ordinal and row index are coordinates
///     local to the snapshot that minted it, not ownership: another index can use the same ordinals, and a
///     rescan can replace the anchor's block so the same coordinates name a different directory. The anchor
///     is therefore accepted only when its own <see cref="DriveBlock" /> is the very block the borrowed
///     snapshot holds for that drive and its row slot still holds the incarnation it was minted for.
///     Comparing the whole snapshot would reject an anchor whenever any other drive was republished.
/// </summary>
internal static class SubtreeAnchor
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
    ///     Resolves the query's anchor against the snapshot the query borrowed, so a publication that lands
    ///     after admission cannot turn the validation into a check-then-use race.
    /// </summary>
    /// <param name="borrowed">The snapshot this query holds a borrow on.</param>
    /// <param name="query">The query whose anchor is resolved.</param>
    /// <returns>The resolved anchor, or null when the query has no subtree restriction.</returns>
    /// <exception cref="ArgumentException">
    ///     The anchor is the default <see cref="FileEntry" />, belongs to another index or to a drive block a
    ///     rescan replaced, or has been superseded by a newer file reusing its row slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The anchor's snapshot has been released.</exception>
    internal static ResolvedSubtree? Resolve(Snapshot borrowed, SearchQuery query)
    {
        if (query.Subtree is not { } anchor)
        {
            return null;
        }

        if (anchor.NamedDriveBlock is not { } anchorBlock)
        {
            throw DefaultAnchor(nameof(query));
        }

        // The borrowed snapshot keeps its own blocks mapped even once a disposal has started releasing it, and
        // that disposal reaches the scan as cancellation. Any other released owner has dropped its blocks.
        if (!ReferenceEquals(anchor.OwnerSnapshot, borrowed))
        {
            ObjectDisposedException.ThrowIf(anchor.IsDisposed, typeof(FileEntry));
        }

        var current = borrowed.FindDriveBlock(anchorBlock.Drive);
        if (!ReferenceEquals(current, anchorBlock))
        {
            throw new ArgumentException(
                $"SearchQuery.Subtree does not belong to the queried index's current block for drive " +
                $"{anchorBlock.Drive}: it came from another index or from a block a rescan replaced. " +
                "Take a new anchor from the index being queried.", nameof(query));
        }

        if (anchor.IsSupersededWhileMapped)
        {
            throw new ArgumentException(
                "SearchQuery.Subtree names an entry whose row slot a newer file has reused after its deletion. " +
                "Take a new anchor from the index being queried.", nameof(query));
        }

        return new ResolvedSubtree(current.DriveOrdinal, anchor.RowIndex);
    }
}
