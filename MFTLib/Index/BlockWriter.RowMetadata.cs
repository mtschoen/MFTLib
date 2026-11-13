namespace MFTLib.Index;

/// <summary>
///     The row-metadata members of <see cref="BlockWriter" />: the journal's reads and updates of an existing row's
///     flags, attributes and scanned observations, each holding the block's access scope.
/// </summary>
sealed partial class BlockWriter
{
    /// <summary>
    ///     Reads a row's state flags as part of the atomic descriptor word, inside an access scope.
    ///     An index past the slot capacity reads as <see cref="RowFlags.None" />.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to read.</param>
    /// <returns>The row's flags.</returns>
    public RowFlags ReadRowFlags(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.SlotCapacity)
        {
            return RowFlags.None;
        }

        return FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in Block.Rows[(int)rowIndex]));
    }

    /// <summary>
    ///     Marks an allocated row deleted while retaining its name for change reporting. When completing a
    ///     <see cref="RowFlags.DeletePending" /> row, restores the bare file name from the last segment of the
    ///     stored pre-move path.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to mark.</param>
    public void MarkTombstone(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        AddRowFlags(rowIndex, RowFlags.Tombstone, ReadOnlySpan<char>.Empty);
    }

    /// <summary>
    ///     Marks an allocated row delete-pending so it is hidden from queries like a tombstone while storing its
    ///     full pre-move path in the name column for the later delete record that completes the tombstone. A row
    ///     becomes delete-pending only when <paramref name="preMovePath" /> is interned into the name column;
    ///     if interning fails, the block is marked capacity-exhausted and the transition is refused without
    ///     changing the row.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to mark.</param>
    /// <param name="preMovePath">The row's full path before the delete-pending rename.</param>
    /// <returns>true when the pre-move path was interned and the row was marked delete-pending; otherwise, false.</returns>
    public bool MarkDeletePending(uint rowIndex, ReadOnlySpan<char> preMovePath)
    {
        using var access = Block.TakeAccess();
        return AddRowFlags(rowIndex, RowFlags.DeletePending, preMovePath);
    }

    /// <summary>Marks a directory whose subtree enumeration was skipped after an access denial.</summary>
    /// <param name="rowIndex">Zero-based directory row slot to mark.</param>
    public void MarkSubtreeSkipped(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        AddRowFlags(rowIndex, RowFlags.SubtreeSkipped, ReadOnlySpan<char>.Empty);
    }

    /// <summary>
    ///     Applies a journal event's metadata to a live row without touching its parent or name:
    ///     the attribute word is replaced, and <paramref name="freshness" /> decides which scanned
    ///     observations survive. An invalidated observation is marked unknown through the descriptor
    ///     word before its column is zeroed, so a reader never sees a zero it was told to trust. A
    ///     A row that is unused or deleted is left untouched.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to update.</param>
    /// <param name="attributes">Raw NTFS file attributes the journal record captured.</param>
    /// <param name="freshness">Which of the row's observations the event invalidates.</param>
    /// <returns>true when any column or flag changed.</returns>
    public bool UpdateRowMetadata(uint rowIndex, uint attributes, RowFreshness freshness)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.SlotCapacity)
        {
            MarkCapacityExhaustedCore();
            return false;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        Hooks._rowCaptured?.Invoke();
        var descriptor = FileRow.ReadDescriptorWord(in row);
        var flags = FileRow.DescriptorFlags(descriptor);
        if ((flags & RowFlags.InUse) == 0 || (flags & FileRow.DeletedFlags) != 0)
        {
            return false;
        }

        var invalidatesLastWrite = freshness != RowFreshness.Unchanged;
        var invalidatesSize = freshness == RowFreshness.ContentInvalidated;
        var updatedFlags = flags;
        if (invalidatesLastWrite)
        {
            updatedFlags |= RowFlags.LastWriteTimeUnknown;
        }

        if (invalidatesSize)
        {
            updatedFlags |= RowFlags.SizeUnknown;
        }

        var changed = updatedFlags != flags;
        if (changed)
        {
            FileRow.WriteDescriptorWord(ref row, FileRow.DescriptorNameId(descriptor), updatedFlags);
        }

        if (invalidatesLastWrite && row.ModifiedTicks != 0)
        {
            row.ModifiedTicks = 0;
            changed = true;
        }

        if (invalidatesSize && row.Size != 0)
        {
            row.Size = 0;
            changed = true;
        }

        if (row.Attributes != attributes)
        {
            row.Attributes = attributes;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    ///     Applies the data size an extension record carries to its already-written base row, with the rule the
    ///     native whole-result parse applies: the row is live, its sequence number matches, it is not a directory
    ///     and its size is still unknown. The size is stored before the descriptor word clears the unknown flag.
    /// </summary>
    /// <returns>true when the size was applied.</returns>
    internal bool TryApplyScannedDataSize(uint rowIndex, ushort sequenceNumber, long size)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.RowCount)
        {
            return false;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        var descriptor = FileRow.ReadDescriptorWord(in row);
        var flags = FileRow.DescriptorFlags(descriptor);
        if ((flags & RowFlags.InUse) == 0 || (flags & FileRow.DeletedFlags) != 0 ||
            (flags & (RowFlags.Directory | RowFlags.SizeUnknown)) != RowFlags.SizeUnknown ||
            Block.SequenceNumbers[(int)rowIndex] != sequenceNumber)
        {
            return false;
        }

        row.Size = size;
        FileRow.WriteDescriptorWord(ref row, FileRow.DescriptorNameId(descriptor), flags & ~RowFlags.SizeUnknown);
        return true;
    }

    /// <summary>
    ///     Sets flag bits on a live row by rewriting the whole descriptor word. Touching the
    ///     flags field on its own would be a second independent store into the same word that a
    ///     rename publishes atomically, which is exactly the tear this layout exists to prevent.
    ///     Callers reach this only from members already holding an access scope.
    /// </summary>
    bool AddRowFlags(uint rowIndex, RowFlags additionalFlags, ReadOnlySpan<char> replacementName)
    {
        if (rowIndex >= Block.SlotCapacity)
        {
            MarkCapacityExhaustedCore();
            return false;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        var descriptor = FileRow.ReadDescriptorWord(in row);
        var flags = FileRow.DescriptorFlags(descriptor);
        var nameId = FileRow.DescriptorNameId(descriptor);
        if ((additionalFlags & RowFlags.DeletePending) != 0)
        {
            if (!TryInternName(replacementName, out nameId))
            {
                return false;
            }
        }
        else if ((additionalFlags & RowFlags.Tombstone) != 0 && (flags & RowFlags.DeletePending) != 0)
        {
            var lastSegment = IndexNavigation.LastPathSegmentString(NamePool.Read(Block, nameId));
            if (lastSegment.Length > 0 && TryInternName(lastSegment, out var restoredNameId))
            {
                nameId = restoredNameId;
            }
        }

        var marksDeleted = (additionalFlags & FileRow.DeletedFlags) != 0;
        if (marksDeleted && (flags & RowFlags.InUse) != 0 && (flags & FileRow.DeletedFlags) == 0)
        {
            Block.Header.LiveRowCount--;
        }

        if (marksDeleted && (flags & FileRow.DeletedFlags) == 0)
        {
            Block._deletedChildren?.AddDeletedRow(rowIndex, row.ParentRow);
        }

        var updatedFlags = (flags | additionalFlags) &
            ((additionalFlags & RowFlags.Tombstone) != 0 ? ~RowFlags.DeletePending : ~RowFlags.None);
        FileRow.WriteDescriptorWord(ref row,
            nameId,
            updatedFlags);
        return true;
    }
}
