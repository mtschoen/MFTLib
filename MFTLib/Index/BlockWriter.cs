namespace MFTLib.Index;

/// <summary>
///     The only type that writes rows and names into a block. Every capacity limit is enforced
///     here: an out-of-range row index or a name that does not fit sets the compaction-needed
///     flag and reports failure, so a producer or a journal batch keeps applying what does fit
///     and the drive is reported stale rather than crashing or silently dropping records.
///     Every public member holds a <see cref="BlockAccessScope" /> on the block for the member's
///     whole duration, so a call racing <see cref="BlockFile.Dispose" /> either completes against
///     mapped memory or, when disposal began first, fails with <see cref="ObjectDisposedException" />
///     instead of dereferencing an unmapped view.
/// </summary>
internal sealed class BlockWriter
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

    /// <summary>
    ///     A test seam, held per instance rather than statically so two writers never share it.
    ///     Invoked in <see cref="TryWriteRow" /> once the target row's reference is captured and
    ///     before its first field access, which is the window in which a racing dispose used to
    ///     unmap the view under the write.
    /// </summary>
    internal Action? _rowCapturedForTest;

    /// <summary>
    ///     A test seam, per instance. Invoked in <see cref="Complete" /> once its access is held and
    ///     before the header is stamped, which is the window in which a dispose can begin while the
    ///     operation is still admitted.
    /// </summary>
    internal Action? _completeAccessTakenForTest;

    /// <summary>Gets the highest written row index plus one, including tombstones and unpopulated gaps.</summary>
    public uint RowCount
    {
        get
        {
            using var access = Block.TakeAccess();
            return Block.Header.RowCount;
        }
    }

    /// <summary>Determines whether the block has exhausted row or name-pool capacity.</summary>
    public bool CompactionNeeded
    {
        get
        {
            using var access = Block.TakeAccess();
            return Block.Header.IsCompactionNeeded;
        }
    }

    /// <summary>
    ///     Fills one slot. Returns false without writing anything when the slot is past capacity
    ///     or the name does not fit, having first set the compaction-needed flag.
    /// </summary>
    /// <param name="rowIndex">Zero-based row slot to populate.</param>
    /// <param name="name">File or directory name to append to the immutable name pool.</param>
    /// <param name="columns">Remaining row values, including parent, attributes, size, and flags.</param>
    /// <returns>true when the row and name fit; otherwise, false after marking compaction needed.</returns>
    public bool TryWriteRow(uint rowIndex, ReadOnlySpan<char> name, in RowColumns columns)
    {
        using var access = Block.TakeAccess();
        ref var header = ref Block.Header;
        if (rowIndex >= header.SlotCapacity)
        {
            MarkCompactionNeededCore();
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
        _rowCapturedForTest?.Invoke();
        var previousFlags = FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in row));
        var wasLive = (previousFlags & RowFlags.InUse) != 0 && (previousFlags & RowFlags.Tombstone) == 0;
        var isLive = (columns.Flags & RowFlags.InUse) != 0 && (columns.Flags & RowFlags.Tombstone) == 0;
        row.ParentRow = columns.ParentRow;
        row.Attributes = columns.Attributes;
        row.Size = columns.Size;
        row.ModifiedTicks = columns.ModifiedTicks;
        Block.SequenceNumbers[(int)rowIndex] = columns.SequenceNumber;
        FileRow.WriteDescriptorWord(ref row, nameOffsetBytes, (ushort)name.Length, columns.Flags);

        if (isLive && !wasLive)
        {
            header.LiveRowCount++;
        }
        else if (!isLive && wasLive)
        {
            header.LiveRowCount--;
        }

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
    /// <param name="rowIndex">Zero-based row slot to rename or move.</param>
    /// <param name="name">New name, or the current name for a parent-only move.</param>
    /// <param name="parentRow">New parent-row index.</param>
    /// <returns>true when the update fits; otherwise, false after marking compaction needed.</returns>
    public bool TryRenameRow(uint rowIndex, ReadOnlySpan<char> name, uint parentRow)
    {
        using var access = Block.TakeAccess();
        if (rowIndex >= Block.Header.SlotCapacity)
        {
            MarkCompactionNeededCore();
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

    /// <summary>Marks an allocated row deleted while retaining its name for change reporting.</summary>
    /// <param name="rowIndex">Zero-based row slot to mark.</param>
    public void MarkTombstone(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        AddRowFlags(rowIndex, RowFlags.Tombstone);
    }

    /// <summary>Marks a directory whose subtree enumeration was skipped after an access denial.</summary>
    /// <param name="rowIndex">Zero-based directory row slot to mark.</param>
    public void MarkSubtreeSkipped(uint rowIndex)
    {
        using var access = Block.TakeAccess();
        AddRowFlags(rowIndex, RowFlags.SubtreeSkipped);
    }

    /// <summary>Marks the block stale because an update could not fit its reserved capacity.</summary>
    public void MarkCompactionNeeded()
    {
        using var access = Block.TakeAccess();
        MarkCompactionNeededCore();
    }

    /// <summary>The unscoped body, for callers already holding an access scope.</summary>
    void MarkCompactionNeededCore()
    {
        Block.Header.Flags |= BlockFlags.CompactionNeeded;
    }

    /// <summary>Stores the journal checkpoint from which this completed block can be watched.</summary>
    /// <param name="journalId">Current USN journal identifier.</param>
    /// <param name="nextUsn">Next unread USN cursor.</param>
    public void SetJournalCursor(ulong journalId, long nextUsn)
    {
        using var access = Block.TakeAccess();
        ref var header = ref Block.Header;
        header.UsnJournalId = journalId;
        header.UsnNextUsn = nextUsn;
    }

    /// <summary>Increments and returns the block's mutation generation.</summary>
    /// <returns>The incremented generation.</returns>
    public ulong BumpGeneration()
    {
        using var access = Block.TakeAccess();
        ref var header = ref Block.Header;
        header.Generation++;
        return header.Generation;
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
        _completeAccessTakenForTest?.Invoke();
        ref var header = ref Block.Header;
        header.ScanTimestampTicks = scanTimestampUtc.Ticks;
        if (header.Generation == 0)
        {
            header.Generation = 1;
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
        if (rowIndex >= Block.Header.SlotCapacity)
        {
            MarkCompactionNeededCore();
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

        FileRow.WriteDescriptorWord(ref row,
            FileRow.DescriptorNameOffsetBytes(descriptor),
            FileRow.DescriptorNameLengthUnits(descriptor),
            flags | additionalFlags);
    }

    /// <summary>Callers reach this only from members already holding an access scope.</summary>
    bool TryAppendName(ReadOnlySpan<char> name, out uint nameOffsetBytes)
    {
        ref var header = ref Block.Header;
        var used = header.NamePoolUsed;
        if (!NamePool.TryAppend(Block.NamePoolCharacters, ref used, header.NamePoolCapacity, name,
                out nameOffsetBytes))
        {
            MarkCompactionNeededCore();
            return false;
        }

        header.NamePoolUsed = used;
        return true;
    }
}
