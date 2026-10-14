namespace MFTLib.Index;

/// <summary>
///     The row-metadata members of <see cref="BlockWriter" />: the journal's reads and updates of an existing row's
///     flags, attributes and scanned observations, each holding the block's access scope.
/// </summary>
internal sealed partial class BlockWriter
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
        if (rowIndex >= Block.Header.SlotCapacity)
        {
            return RowFlags.None;
        }

        return FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in Block.Rows[(int)rowIndex]));
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
        if (rowIndex >= Block.Header.SlotCapacity)
        {
            MarkCompactionNeededCore();
            return false;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        _rowCapturedForTest?.Invoke();
        var descriptor = FileRow.ReadDescriptorWord(in row);
        var flags = FileRow.DescriptorFlags(descriptor);
        if ((flags & RowFlags.InUse) == 0 || (flags & RowFlags.Tombstone) != 0)
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
            FileRow.WriteDescriptorWord(ref row, FileRow.DescriptorNameOffsetBytes(descriptor),
                FileRow.DescriptorNameLengthUnits(descriptor), updatedFlags);
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
}
