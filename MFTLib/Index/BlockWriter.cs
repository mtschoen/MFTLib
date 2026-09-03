namespace MFTLib.Index;

/// <summary>
///     The only type that writes rows and names into a block. Every capacity limit is enforced
///     here: an out-of-range row index or a name that does not fit sets the compaction-needed
///     flag and reports failure, so a producer or a journal batch keeps applying what does fit
///     and the drive is reported stale rather than crashing or silently dropping records.
/// </summary>
public sealed class BlockWriter
{
    public BlockWriter(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
    }

    public BlockFile Block { get; }

    public uint RowCount => Block.Header.RowCount;

    public bool CompactionNeeded => Block.Header.IsCompactionNeeded;

    /// <summary>
    ///     Fills one slot. Returns false without writing anything when the slot is past capacity
    ///     or the name does not fit, having first set the compaction-needed flag.
    /// </summary>
    public bool TryWriteRow(uint rowIndex, ReadOnlySpan<char> name, in RowColumns columns)
    {
        ref var header = ref Block.Header;
        if (rowIndex >= header.SlotCapacity)
        {
            MarkCompactionNeeded();
            return false;
        }

        if (!TryAppendName(name, out var nameOffsetBytes))
        {
            return false;
        }

        // A create can reuse the row index of a previously deleted record, which is inside
        // the published range a reader may already be scanning. Every field except the
        // descriptor word is written first, then the name offset, name length, and flags are
        // published together as one atomic store, exactly as a rename publishes them. That
        // ordering holds for a fresh slot too, so there is no separate unpublished-slot case.
        ref var row = ref Block.Rows[(int)rowIndex];
        row.ParentRow = columns.ParentRow;
        row.Attributes = columns.Attributes;
        row.Size = columns.Size;
        row.ModifiedTicks = columns.ModifiedTicks;
        FileRow.WriteDescriptorWord(ref row, nameOffsetBytes, (ushort)name.Length, columns.Flags);

        if (rowIndex >= header.RowCount)
        {
            header.RowCount = rowIndex + 1;
        }

        return true;
    }

    /// <summary>
    ///     A move whose name is unchanged writes only the parent row and leaves the descriptor
    ///     word untouched, so a move-heavy workload does not re-append the same name on every
    ///     move and burn name-pool space toward an early compaction. A real rename (the name
    ///     differs, including an exact-case-only change) appends the new name, sets the parent,
    ///     and only then publishes the new name offset and length as one atomic descriptor-word
    ///     store. That ordering plus the single store is the format's guarantee that a
    ///     concurrent reader sees the old name or the new one and never a torn pairing of one
    ///     name's offset with another name's length.
    /// </summary>
    public bool TryRenameRow(uint rowIndex, ReadOnlySpan<char> name, uint parentRow)
    {
        if (rowIndex >= Block.Header.SlotCapacity)
        {
            MarkCompactionNeeded();
            return false;
        }

        var currentName = NamePool.ReadRowName(Block, rowIndex);
        ref var row = ref Block.Rows[(int)rowIndex];
        if (NameMatching.EqualsName(name, currentName, caseSensitive: true))
        {
            row.ParentRow = parentRow;
            return true;
        }

        if (!TryAppendName(name, out var nameOffsetBytes))
        {
            return false;
        }

        var flags = FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in row));
        row.ParentRow = parentRow;
        FileRow.WriteDescriptorWord(ref row, nameOffsetBytes, (ushort)name.Length, flags);
        return true;
    }

    public void MarkTombstone(uint rowIndex)
    {
        AddRowFlags(rowIndex, RowFlags.Tombstone);
    }

    public void MarkSubtreeSkipped(uint rowIndex)
    {
        AddRowFlags(rowIndex, RowFlags.SubtreeSkipped);
    }

    public void MarkCompactionNeeded()
    {
        Block.Header.Flags |= BlockFlags.CompactionNeeded;
    }

    public void SetJournalCursor(ulong journalId, long nextUsn)
    {
        ref var header = ref Block.Header;
        header.UsnJournalId = journalId;
        header.UsnNextUsn = nextUsn;
    }

    public ulong BumpGeneration()
    {
        ref var header = ref Block.Header;
        header.Generation++;
        return header.Generation;
    }

    /// <summary>
    ///     Stamps the scan timestamp and sets the complete flag last, then flushes. A producer
    ///     that dies before this call leaves a block that validation rejects.
    /// </summary>
    public void Complete(DateTime scanTimestampUtc)
    {
        ref var header = ref Block.Header;
        header.ScanTimestampTicks = scanTimestampUtc.Ticks;
        if (header.Generation == 0)
        {
            header.Generation = 1;
        }

        header.Flags |= BlockFlags.Complete;
        Block.Flush();
    }

    /// <summary>
    ///     Sets flag bits on a live row by rewriting the whole descriptor word. Touching the
    ///     flags field on its own would be a second independent store into the same word that a
    ///     rename publishes atomically, which is exactly the tear this layout exists to prevent.
    /// </summary>
    void AddRowFlags(uint rowIndex, RowFlags additionalFlags)
    {
        if (rowIndex >= Block.Header.SlotCapacity)
        {
            MarkCompactionNeeded();
            return;
        }

        ref var row = ref Block.Rows[(int)rowIndex];
        var descriptor = FileRow.ReadDescriptorWord(in row);
        FileRow.WriteDescriptorWord(ref row,
            FileRow.DescriptorNameOffsetBytes(descriptor),
            FileRow.DescriptorNameLengthUnits(descriptor),
            FileRow.DescriptorFlags(descriptor) | additionalFlags);
    }

    bool TryAppendName(ReadOnlySpan<char> name, out uint nameOffsetBytes)
    {
        ref var header = ref Block.Header;
        var used = header.NamePoolUsed;
        if (!NamePool.TryAppend(Block.NamePoolCharacters, ref used, header.NamePoolCapacity, name,
                out nameOffsetBytes))
        {
            MarkCompactionNeeded();
            return false;
        }

        header.NamePoolUsed = used;
        return true;
    }
}
