namespace MFTLib.Index;

/// <summary>
///     Writes rows and names; a row or name that cannot fit sets the capacity-exhausted flag and
///     reports failure, allowing callers to keep what fits and report the need for a rescan.
///     Each public member holds a <see cref="BlockAccessScope" /> throughout: it either completes
///     before unmapping or throws <see cref="ObjectDisposedException" /> when disposal began first.
/// </summary>
sealed partial class BlockWriter
{
    /// <summary>Initializes a writer for one mapped block.</summary>
    /// <param name="block">Block that receives rows, names, and header updates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="block" /> is null.</exception>
    public BlockWriter(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
    }

    /// <summary>Provides the mapped block that each writer operation keeps accessible during its lifetime.</summary>
    public BlockFile Block { get; }

    /// <summary>Gets the highest written row index plus one, including tombstones and unpopulated gaps.</summary>
    public uint RowCount
    {
        get
        {
            using var access = Block.TakeAccess();
            return Block.RowCount;
        }
    }

    /// <summary>The row slot count fixed at creation; a journal record for a record number at or past it cannot be stored.</summary>
    public uint SlotCapacity
    {
        get
        {
            using var access = Block.TakeAccess();
            return Block.SlotCapacity;
        }
    }

    /// <summary>Determines whether the block has exhausted row, name-entry or name-pool capacity.</summary>
    public bool CapacityExhausted
    {
        get
        {
            using var access = Block.TakeAccess();
            return Block.Header.IsCapacityExhausted;
        }
    }

    /// <summary>Fills one slot, or sets the capacity-exhausted flag and returns false without writing a row when the slot or name cannot fit.</summary>
    /// <param name="rowIndex">Zero-based row slot to populate.</param>
    /// <param name="name">File or directory name to intern in the block's name pool.</param>
    /// <param name="columns">Remaining row values, including parent, attributes, size, and flags.</param>
    /// <returns>true when the row and name fit; otherwise, false after marking capacity exhausted.</returns>
    public bool TryWriteRow(uint rowIndex, ReadOnlySpan<char> name, in RowColumns columns)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.SlotCapacity)
        {
            MarkCapacityExhaustedCore();
            return false;
        }

        if (!TryInternName(name, out var nameId))
        {
            return false;
        }

        // A create can reuse the row index of a previously deleted record, which is inside
        // the published range a reader may already be scanning. Every field except the
        // descriptor word is written first, then the name identifier and flags are
        // published together as one atomic store, exactly as a rename publishes them. That
        // ordering holds for a fresh slot too, so there is no separate unpublished-slot case.
        ref var row = ref Block.Rows[(int)rowIndex];
        Hooks._rowCaptured?.Invoke();
        var previousFlags = FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in row));
        var previousParentRow = row.ParentRow;
        var wasLive = (previousFlags & RowFlags.InUse) != 0 && (previousFlags & RowFlags.Tombstone) == 0;
        var isLive = (columns.Flags & RowFlags.InUse) != 0 && (columns.Flags & RowFlags.Tombstone) == 0;
        // The supplied NTFS sequence is stored as given and published before any column changes, so a handle
        // minted for a different file reference is refused from the first overwritten field on.
        Block.SequenceNumbers[(int)rowIndex] = columns.SequenceNumber;
        row.ParentRow = columns.ParentRow;
        row.Attributes = columns.Attributes;
        row.Size = columns.Size;
        row.ModifiedTicks = columns.ModifiedTicks;
        FileRow.WriteDescriptorWord(ref row, nameId, columns.Flags);

        ref var header = ref Block.Header;
        if (isLive && !wasLive)
        {
            header.LiveRowCount++;
        }
        else if (!isLive && wasLive)
        {
            header.LiveRowCount--;
        }

        if (rowIndex >= Block.RowCount)
        {
            Block.PublishRowCount(rowIndex + 1);
        }

        if (Block._deletedChildren is { } links)
        {
            // A create can reuse a deleted row's slot: drop the row's old link before linking its
            // new shape, so the links always mirror the rows as written.
            if ((previousFlags & RowFlags.Tombstone) != 0)
            {
                links.RemoveDeletedRow(rowIndex, previousParentRow);
            }

            if ((columns.Flags & RowFlags.Tombstone) != 0)
            {
                links.AddDeletedRow(rowIndex, columns.ParentRow);
            }
        }

        return true;
    }

    /// <summary>
    ///     A move whose name is unchanged writes only the parent row and leaves the descriptor
    ///     word untouched. A real rename (the name differs ordinally, so a case-only change counts)
    ///     interns the new spelling, sets the parent, and only then publishes the new name
    ///     identifier and the flags as one atomic descriptor-word store. Peers that share the old
    ///     spelling keep it, because entries are immutable. That ordering plus the single store is
    ///     the format's guarantee that a concurrent reader sees the old name or the new one.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to rename or move.</param>
    /// <param name="name">New name, or the current name for a parent-only move.</param>
    /// <param name="parentRow">New parent-row index.</param>
    /// <returns>true when the update fits; otherwise, false after marking capacity exhausted.</returns>
    public bool TryRenameRow(uint rowIndex, ReadOnlySpan<char> name, uint parentRow)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.SlotCapacity)
        {
            MarkCapacityExhaustedCore();
            return false;
        }

        var currentName = NamePool.ReadRowName(Block, rowIndex);
        ref var row = ref Block.Rows[(int)rowIndex];
        var previousParentRow = row.ParentRow;
        var wasDeleted = row.IsDeleted;
        if (currentName.EqualsOrdinal(name))
        {
            row.ParentRow = parentRow;
            RelinkDeletedRow(rowIndex, wasDeleted, previousParentRow, parentRow);
            return true;
        }

        if (!TryInternName(name, out var nameId))
        {
            return false;
        }

        var flags = FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in row));
        row.ParentRow = parentRow;
        RelinkDeletedRow(rowIndex, wasDeleted, previousParentRow, parentRow);
        FileRow.WriteDescriptorWord(ref row, nameId, flags);
        return true;
    }

    /// <summary>
    ///     Keeps the block's deleted-child links consistent across a parent change. Only a deleted
    ///     row is linked, and only a create, delete, or detach changes whether a row is deleted;
    ///     the journal never renames a deleted row, but a direct writer call can, so the relink is
    ///     unconditional rather than assumed away.
    /// </summary>
    void RelinkDeletedRow(uint rowIndex, bool wasDeleted, uint previousParentRow, uint parentRow)
    {
        if (!wasDeleted || previousParentRow == parentRow || Block._deletedChildren is not { } links)
        {
            return;
        }

        links.RemoveDeletedRow(rowIndex, previousParentRow);
        links.AddDeletedRow(rowIndex, parentRow);
    }

    /// <summary>Marks an allocated row deleted while retaining its name for change reporting.</summary>
    /// <param name="rowIndex">Zero-based row slot to mark.</param>
    public void MarkTombstone(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        AddRowFlags(rowIndex, RowFlags.Tombstone);
    }

    /// <summary>
    ///     Points a row at <see cref="BlockLayout.DetachedParentRow" />: its parent could not be verified, so it is
    ///     nobody's child and its path is its bare name. The parent column is a plain 32-bit field, so the store
    ///     is atomic by itself and leaves the descriptor word alone.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to detach.</param>
    public void DetachRow(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.SlotCapacity)
        {
            MarkCapacityExhaustedCore();
            return;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        var previousParentRow = row.ParentRow;
        row.ParentRow = BlockLayout.DetachedParentRow;
        if (row.IsDeleted)
        {
            // The row's own deleted children stay linked under it: reusing its slot must still
            // find them. Only the link from its old parent is out of reach now.
            Block._deletedChildren?.RemoveDeletedRow(rowIndex, previousParentRow);
        }
    }

    /// <summary>
    ///     Invalidates affected deleted descendant links when journal creation reuses an ancestor slot:
    ///     detaches any deleted row whose parent chain reaches <paramref name="ancestorRow" />
    ///     through deleted rows, so its path becomes its bare name, its parent is null, and subtree
    ///     queries exclude it. The work is proportional to those descendants, not to the block's
    ///     row count: <see cref="DeletedChildLinks" /> indexes deleted rows by parent once per
    ///     block and every writer mutation keeps the index current, so a create on a fresh slot or
    ///     on an all-live index costs one lookup. A deleted row whose chain reaches the slot only
    ///     through a live row keeps its parent; a live row that names a deleted or never-written
    ///     slot as its parent is a shape only a corrupt image or journal produces.
    /// </summary>
    public void InvalidateDeletedDescendants(uint ancestorRow)
    {
        using var access = Block.TakeAccess();
        if (ancestorRow >= Block.RowCount)
        {
            return;
        }

        var links = Block._deletedChildren ??= DeletedChildLinks.Build(Block);
        links.DetachDescendants(Block, ancestorRow);
    }

    /// <summary>Marks a directory whose subtree enumeration was skipped after an access denial.</summary>
    /// <param name="rowIndex">Zero-based directory row slot to mark.</param>
    public void MarkSubtreeSkipped(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        AddRowFlags(rowIndex, RowFlags.SubtreeSkipped);
    }

    /// <summary>Marks the block stale because an update could not fit its reserved capacity.</summary>
    public void MarkCapacityExhausted()
    {
        using var access = Block.TakeAccess();
        MarkCapacityExhaustedCore();
    }

    /// <summary>The unscoped body, for callers already holding an access scope.</summary>
    void MarkCapacityExhaustedCore()
    {
        Block.Header.Flags |= BlockFlags.CapacityExhausted;
    }

    /// <summary>Stores the journal checkpoint from which this completed block can be watched.</summary>
    /// <param name="journalIdentifier">Current USN journal identifier.</param>
    /// <param name="nextUsn">Next unread USN cursor.</param>
    public void SetJournalCursor(ulong journalIdentifier, long nextUsn)
    {
        using var access = Block.TakeAccess();
        ref var header = ref Block.Header;
        header.UsnJournalIdentifier = journalIdentifier;
        header.UsnNextUsn = nextUsn;
    }

    /// <summary>Stores the enumeration producer's count of subtrees it could not enter, so a warm start reports it.</summary>
    /// <param name="count">The walk's access-denied subtree count.</param>
    public void SetAccessDeniedSubtreeCount(uint count)
    {
        using var access = Block.TakeAccess();
        Block.Header.AccessDeniedSubtreeCount = count;
    }

    /// <summary>Increments and returns the block's mutation generation.</summary>
    /// <returns>The incremented generation.</returns>
    public ulong BumpGeneration()
    {
        using var access = Block.TakeAccess();
        var next = Block.Generation + 1;
        Block.PublishGeneration(next);
        return next;
    }

    /// <summary>
    ///     Raises the generation of a replacement block above the superseded block's, so a root's
    ///     generation never repeats across a rescan. A generation already higher is kept.
    /// </summary>
    /// <param name="supersededGeneration">The generation of the block this one replaces.</param>
    public void AdvanceGenerationPast(ulong supersededGeneration)
    {
        using var access = Block.TakeAccess();
        Block.PublishGeneration(Math.Max(Block.Generation, supersededGeneration + 1));
    }

    /// <summary>
    ///     Stamps the scan timestamp and sets the complete flag last, then flushes, reporting cumulative
    ///     bytes flushed to <paramref name="rangeFlushed" /> after each range when it is not null. A producer
    ///     that dies before this call leaves a block that validation rejects.
    /// </summary>
    /// <param name="scanTimestampUtc">UTC time at which production completed.</param>
    /// <param name="rangeFlushed">Optional callback receiving cumulative bytes flushed so far after each range.</param>
    public void Complete(DateTime scanTimestampUtc, Action<long>? rangeFlushed)
    {
        using var access = Block.TakeAccess();
        Hooks._completeAccessTaken?.Invoke();
        ref var header = ref Block.Header;
        header.ScanTimestampTicks = scanTimestampUtc.Ticks;
        if (Block.Generation == 0)
        {
            Block.PublishGeneration(1);
        }

        header.Flags |= BlockFlags.Complete;
        Block.FlushUnderHeldAccess(rangeFlushed);
    }

    /// <summary>
    ///     Sets flag bits on a live row by rewriting the whole descriptor word. Touching the
    ///     flags field on its own would be a second independent store into the same word that a
    ///     rename publishes atomically, which is exactly the tear this layout exists to prevent.
    ///     Callers reach this only from members already holding an access scope.
    /// </summary>
    void AddRowFlags(uint rowIndex, RowFlags additionalFlags)
    {
        if (rowIndex >= Block.SlotCapacity)
        {
            MarkCapacityExhaustedCore();
            return;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        var descriptor = FileRow.ReadDescriptorWord(in row);
        var flags = FileRow.DescriptorFlags(descriptor);
        if (additionalFlags.HasFlag(RowFlags.Tombstone) &&
            (flags & RowFlags.InUse) != 0 && (flags & RowFlags.Tombstone) == 0)
        {
            Block.Header.LiveRowCount--;
        }

        if (additionalFlags.HasFlag(RowFlags.Tombstone) && (flags & RowFlags.Tombstone) == 0)
        {
            Block._deletedChildren?.AddDeletedRow(rowIndex, row.ParentRow);
        }

        FileRow.WriteDescriptorWord(ref row,
            FileRow.DescriptorNameId(descriptor),
            flags | additionalFlags);
    }

    /// <summary>Callers reach this only from members already holding an access scope.</summary>
    bool TryInternName(ReadOnlySpan<char> name, out uint nameId)
    {
        if (!NamePool.TryGetOrAdd(Block, name, out nameId))
        {
            MarkCapacityExhaustedCore();
            return false;
        }

        return true;
    }
}
