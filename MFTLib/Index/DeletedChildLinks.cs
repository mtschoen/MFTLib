namespace MFTLib.Index;

/// <summary>
///     Reverse links from a parent row to the deleted rows that name it as their parent, kept per
///     block so a journal create that reuses a slot detaches exactly the deleted subtree below that
///     slot instead of scanning every row in the block. The links are built lazily from the rows as
///     written the first time an invalidation needs them: a block opened from a cache file, or
///     filled by a writer that never invalidated, carries no managed state to reuse. Once built,
///     every <see cref="BlockWriter" /> mutation that writes, tombstones, renames, or detaches a
///     row keeps the links current, and <see cref="BlockWriter" /> is the only type that writes
///     rows, so no change can bypass the index. A deleted row is linked once, under its current
///     parent; a row whose parent is <see cref="BlockLayout.DetachedParentRow" /> is not linked,
///     because no reused slot can reach it through a deleted-parent chain. The instance lives on
///     <see cref="BlockFile" /> rather than on a writer because a watch creates a fresh writer per
///     journal batch, and per-writer links would be rebuilt for every batch.
/// </summary>
internal sealed class DeletedChildLinks
{
    readonly Dictionary<uint, HashSet<uint>> _childrenByParent = [];

    internal LinkObservations Observations { get; } = new();

    /// <summary>Per-instance work counters for this deleted-links table.</summary>
    internal sealed class LinkObservations
    {
        /// <summary>Deleted rows <see cref="DetachDescendants" /> has visited; the build pass is not counted.</summary>
        internal long DescendantsVisited;

        /// <summary>Sibling entries <see cref="RemoveDeletedRow" /> examines while unlinking a row.</summary>
        internal long SiblingsExamined;
    }

    /// <summary>
    ///     Indexes every deleted, non-detached row under its parent in one pass over the written
    ///     rows. Callers hold a block access scope.
    /// </summary>
    public static DeletedChildLinks Build(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var links = new DeletedChildLinks();
        var rowCount = block.Header.RowCount;
        var rows = block.Rows;
        for (var rowIndex = 0u; rowIndex < rowCount; rowIndex++)
        {
            ref readonly var row = ref rows[(int)rowIndex];
            if (row.IsDeleted && row.ParentRow != BlockLayout.DetachedParentRow)
            {
                links.AddDeletedRow(rowIndex, row.ParentRow);
            }
        }

        return links;
    }

    /// <summary>Links a row that just became deleted under its parent.</summary>
    public void AddDeletedRow(uint rowIndex, uint parentRow)
    {
        if (parentRow == BlockLayout.DetachedParentRow)
        {
            return;
        }

        if (!_childrenByParent.TryGetValue(parentRow, out var children))
        {
            children = [];
            _childrenByParent[parentRow] = children;
        }

        children.Add(rowIndex);
    }

    /// <summary>
    ///     Drops the link of a row that stopped being deleted (a create reused its slot) or moved
    ///     out of reach (<see cref="BlockWriter.DetachRow" />). A missing link is tolerated: the
    ///     row may never have been linked, for example because its parent was already detached.
    /// </summary>
    public void RemoveDeletedRow(uint rowIndex, uint parentRow)
    {
        if (parentRow == BlockLayout.DetachedParentRow ||
            !_childrenByParent.TryGetValue(parentRow, out var children))
        {
            return;
        }

        Observations.SiblingsExamined++;
        if (children.Remove(rowIndex) && children.Count == 0)
        {
            _childrenByParent.Remove(parentRow);
        }
    }

    /// <summary>
    ///     Detaches every deleted row below <paramref name="ancestorRow" /> through deleted-parent
    ///     links: a create reused the slot, so a deleted row that still names it (or names a
    ///     deleted row below it) belonged to the slot's previous occupant and must not hang under
    ///     the replacement. Only the affected descendants are visited: each link is consumed as it
    ///     is walked, and a deleted row with no deleted children of its own has no key to walk
    ///     into. Callers hold a block access scope.
    /// </summary>
    public void DetachDescendants(BlockFile block, uint ancestorRow)
    {
        if (!_childrenByParent.TryGetValue(ancestorRow, out _))
        {
            return;
        }

        var pending = new Stack<uint>();
        pending.Push(ancestorRow);
        do
        {
            var current = pending.Pop();
            if (!_childrenByParent.Remove(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child == ancestorRow)
                {
                    // The reused slot is live again; a corrupt self-link must not detach it.
                    continue;
                }

                block.Rows[(int)child].ParentRow = BlockLayout.DetachedParentRow;
                Observations.DescendantsVisited++;
                pending.Push(child);
            }
        }
        while (pending.Count > 0);
    }
}
