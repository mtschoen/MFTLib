namespace MFTLib.Index;

/// <summary>
///     Applies USN journal batches to a block in place. Handed-out handles never dangle and
///     never read garbage; their values simply become current. A journal record carries only
///     its own event time, never the file's size or last-write time, so the mutator keeps those
///     scanned observations when an event cannot have changed them and marks them unknown when
///     it can have; every row write goes through <see cref="BlockWriter" />, which holds the
///     block's access scope. The batch also holds an access scope across incarnation checks,
///     handle creation and event path reads between writer calls. Capacity exhaustion sets the
///     capacity-exhausted flag, keeps applying what fits, and leaves the volume queryable rather
///     than crashing or silently dropping a record. NTFS closes every open cycle with a record
///     that repeats the cycle's reasons plus <see cref="UsnReason.Close" />; the mutator
///     coalesces that close record against the reasons the cycle already reported (tracked per
///     root block in <see cref="ReportedReasonCycles" />), so one real transition raises one
///     change while the row still takes the close record's attributes.
///     A data write that happens inside the create cycle (a record whose reasons still include
///     <see cref="UsnReason.FileCreate" /> before the cycle's close) is part of the creation
///     and raises nothing extra.
///     Suppression is keyed on what the cycle applied, not on the reason bit alone: a second
///     rename inside one open cycle (NTFS writes one record pair per rename and requires no
///     close between renames) carries a new name or parent and classifies again; only the
///     repetition of the applied name and parent is the echo.
/// </summary>
sealed class JournalMutator
{
    const uint VolumeRootRecordNumber = 5;

    internal delegate bool DeletePendingRenameDetector(BlockFile block, ulong newParentRecordNumber, string newName);

    readonly DeletePendingRenameDetector _isDeletePendingRename;

    public JournalMutator(BlockWriter writer, DeletePendingRenameDetector? isDeletePendingRename = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Writer = writer;
        _isDeletePendingRename = isDeletePendingRename ?? IsExtendDeletedTarget;
    }

    public BlockWriter Writer { get; }

    internal MutationHooks Hooks { get; } = new();

    /// <summary>Per-mutator observation immediately before modification path materialization.</summary>
    internal sealed class MutationHooks
    {
        internal Action? _beforeModificationPath;
    }

    public bool CapacityExhausted => Writer.CapacityExhausted;

    /// <summary>
    ///     Applies one batch in order, writes the USN cursor into the header, and bumps the
    ///     generation once if anything actually changed. Entries are applied sequentially rather
    ///     than grouped, because a create and a delete for one record can share a batch.
    /// </summary>
    public IReadOnlyList<FileChange> Apply(RootBlock rootBlock,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        ArgumentNullException.ThrowIfNull(rootBlock);
        ArgumentNullException.ThrowIfNull(entries);

        // Writer methods protect only their own accesses. Keep incarnation reads, handle creation
        // and event path materialization mapped across the gaps between those methods as well.
        using var access = Writer.Block.TakeAccess();
        var changes = new List<FileChange>();
        var rowMutated = false;
        foreach (var entry in entries)
        {
            var change = ApplyOne(rootBlock, entry, ref rowMutated);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        Writer.SetJournalCursor(journalIdentifier, nextUsn);
        if (changes.Count > 0 || rowMutated)
        {
            Writer.BumpGeneration();
        }

        return changes;
    }

    FileChange? ApplyOne(RootBlock rootBlock, UsnJournalEntry entry, ref bool rowMutated)
    {
        var rowIndex = (uint)entry.RecordNumber;
        if (entry.RecordNumber > uint.MaxValue || rowIndex >= Writer.SlotCapacity)
        {
            Writer.MarkCapacityExhausted();
            return null;
        }

        // NTFS accumulates reason flags while a file handle is open and closes it with a final
        // record that repeats every reason already delivered for that open cycle plus
        // USN_REASON_CLOSE. Every record in the cycle (intermediate or close) is therefore
        // classified only by the reasons this cycle has not reported yet; bits already reported
        // must not classify a subsequent record a second time. A data write inside the create
        // cycle is part of the creation and raises nothing extra, so a cycle that reported
        // FileCreate treats all modification reasons as reported. RenameOldName never
        // classifies: it is the paired frame whose RenameNewName sibling carries the rename.
        var cycles = rootBlock.ReportedCycles;
        var reported = cycles.GetReportedReasons(rowIndex, entry.SequenceNumber);
        var meaningful = entry.Reason & ~(UsnReason.Close | UsnReason.RenameOldName);
        var classification = meaningful & ~reported;

        // Only the exact repetition of the name and parent this cycle already applied is a rename echo;
        // a different name or parent inside the same open cycle classifies as the new rename it is.
        if ((classification & UsnReason.RenameNewName) == 0
            && (meaningful & UsnReason.RenameNewName) != 0
            && !cycles.IsReportedRenameEcho(rowIndex, entry.SequenceNumber, entry.FileName,
                entry.ParentRecordNumber))
        {
            classification |= UsnReason.RenameNewName;
        }

        FileChange? change;
        if ((classification & UsnReason.FileCreate) == 0 && IsEarlierIncarnation(rowIndex, entry))
        {
            change = null;
        }
        else if ((classification & UsnReason.FileCreate) != 0)
        {
            change = ApplyCreate(rootBlock, entry, rowIndex);
        }
        else if ((classification & UsnReason.FileDelete) != 0)
        {
            change = ApplyDelete(rootBlock, entry, rowIndex);
        }
        else if ((classification & UsnReason.RenameNewName) != 0)
        {
            change = ApplyRenameArm(rootBlock, entry, rowIndex, classification, ref rowMutated);
        }
        else if (classification != UsnReason.None)
        {
            change = ApplyModification(rootBlock, entry, rowIndex, classification);
        }
        else
        {
            change = null;
            if (entry.IsClose && ApplyCloseMetadata(entry, rowIndex))
            {
                rowMutated = true;
            }
        }

        if (entry.IsClose)
        {
            cycles.CloseCycle(rowIndex);
        }
        else if (meaningful != UsnReason.None)
        {
            cycles.MarkReported(rowIndex, entry.SequenceNumber, meaningful, entry.FileName,
                entry.ParentRecordNumber);
        }

        return change;
    }

    FileChange? ApplyDelete(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex)
    {
        var flags = Writer.ReadRowFlags(rowIndex);
        if ((flags & RowFlags.Tombstone) != 0 || !TryHydrateRow(entry, rowIndex, out _))
        {
            return null;
        }

        // A pending row stores its pre-move full path verbatim in the name column, so completing
        // the tombstone never walks a parent chain that may have been renamed or reused since.
        var path = (flags & RowFlags.DeletePending) != 0
            ? NamePool.ReadRowName(Writer.Block, rowIndex).ToString()
            : IndexNavigation.BuildPath(rootBlock, rowIndex);
        Writer.MarkTombstone(rowIndex);
        return new FileChange(FileChangeKind.Deleted, FileEntry.Create(rootBlock, rowIndex), path,
            entry.TimestampUtc);
    }

    /// <summary>
    ///     The rename arm of <see cref="ApplyOne" />: a rename of a row the index never
    ///     had is reported as a create, because there is no real previous path to give it.
    /// </summary>
    FileChange? ApplyRenameArm(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification, ref bool rowMutated)
    {
        if (IsDeletedRow(rowIndex) || !TryHydrateRow(entry, rowIndex, out var hydrated))
        {
            return null;
        }

        if (hydrated)
        {
            return new FileChange(FileChangeKind.Created, FileEntry.Create(rootBlock, rowIndex),
                IndexNavigation.BuildPath(rootBlock, rowIndex), entry.TimestampUtc);
        }

        return ApplyRename(rootBlock, entry, rowIndex, classification, ref rowMutated);
    }

    /// <summary>
    ///     Applies a close record's attributes without a change notification when its cycle already
    ///     reported every meaningful reason. Scanned size and last-write observations are preserved,
    ///     and unused or deleted slots stay untouched.
    /// </summary>
    bool ApplyCloseMetadata(UsnJournalEntry entry, uint rowIndex)
    {
        return Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, RowFreshness.Unchanged);
    }

    FileChange? ApplyCreate(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex)
    {
        var columns = BuildColumns(entry);
        if (!Writer.TryWriteRow(rowIndex, entry.FileName, in columns))
        {
            return null;
        }

        Writer.InvalidateDeletedDescendants(rowIndex);

        return new FileChange(FileChangeKind.Created, FileEntry.Create(rootBlock, rowIndex),
            IndexNavigation.BuildPath(rootBlock, rowIndex), entry.TimestampUtc);
    }

    FileChange? ApplyRename(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification, ref bool rowMutated)
    {
        var previousPath = IndexNavigation.BuildPath(rootBlock, rowIndex);
        if (_isDeletePendingRename(Writer.Block, entry.ParentRecordNumber, entry.FileName))
        {
            if (Writer.MarkDeletePending(rowIndex, previousPath))
            {
                return new FileChange(FileChangeKind.DeletePending,
                    FileEntry.Create(rootBlock, rowIndex), previousPath, entry.TimestampUtc);
            }
        }
        else if (Writer.TryRenameRow(rowIndex, entry.FileName, (uint)entry.ParentRecordNumber))
        {
            Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification));
            return new FileChange(FileChangeKind.Renamed,
                FileEntry.Create(rootBlock, rowIndex), IndexNavigation.BuildPath(rootBlock, rowIndex),
                entry.TimestampUtc, previousPath);
        }

        if (Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification)))
        {
            rowMutated = true;
        }

        return null;
    }

    /// <summary>
    ///     Handles any reason other than create, delete, or rename, updating attributes and
    ///     invalidating scanned observations per <see cref="FreshnessFor" />.
    /// </summary>
    FileChange? ApplyModification(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification)
    {
        if (IsDeletedRow(rowIndex) || !TryHydrateRow(entry, rowIndex, out _))
        {
            return null;
        }

        Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification));
        Hooks._beforeModificationPath?.Invoke();
        return new FileChange(FileChangeKind.Modified, FileEntry.Create(rootBlock, rowIndex),
            IndexNavigation.BuildPath(rootBlock, rowIndex), entry.TimestampUtc);
    }

    static RowFreshness FreshnessFor(UsnReason classification)
    {
        const UsnReason contentReasons = UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.DataTruncation;
        if ((classification & contentReasons) != 0)
        {
            return RowFreshness.ContentInvalidated;
        }

        return (classification & UsnReason.BasicInfoChange) != 0
            ? RowFreshness.LastWriteInvalidated
            : RowFreshness.Unchanged;
    }

    /// <summary>
    ///     True when the row holds a file whose sequence number differs from the record's: the record belongs to
    ///     another incarnation of the record number and must not mutate this one.
    /// </summary>
    bool IsEarlierIncarnation(uint rowIndex, UsnJournalEntry entry)
    {
        ref readonly var row = ref Writer.Block.Rows[(int)rowIndex];
        var stored = Writer.Block.SequenceNumbers[(int)rowIndex];
        return row.IsInUse && (row.Flags & RowFlags.Tombstone) == 0 && stored != 0 && stored != entry.SequenceNumber;
    }

    bool IsDeletedRow(uint rowIndex)
    {
        return (Writer.ReadRowFlags(rowIndex) & FileRow.DeletedFlags) != 0;
    }

    /// <summary>
    ///     Default delete-pending detector: true when the new parent row is named <c>$Deleted</c> under a row named
    ///     <c>$Extend</c> whose parent is the volume root (record 5).
    /// </summary>
    static bool IsExtendDeletedTarget(BlockFile block, ulong newParentRecordNumber, string newName)
    {
        _ = newName;
        if (newParentRecordNumber >= block.RowCount || newParentRecordNumber == VolumeRootRecordNumber)
        {
            return false;
        }

        var deletedRowIndex = (uint)newParentRecordNumber;
        ref readonly var deletedRow = ref block.Rows[(int)deletedRowIndex];
        if (!deletedRow.IsInUse || deletedRow.IsDeleted || !RowNameEquals(block, deletedRowIndex, "$Deleted"))
        {
            return false;
        }

        var extendRowIndex = deletedRow.ParentRow;
        if (extendRowIndex == deletedRowIndex || extendRowIndex == VolumeRootRecordNumber ||
            extendRowIndex >= block.RowCount)
        {
            return false;
        }

        ref readonly var extendRow = ref block.Rows[(int)extendRowIndex];
        if (!extendRow.IsInUse || extendRow.IsDeleted || !RowNameEquals(block, extendRowIndex, "$Extend"))
        {
            return false;
        }

        var rootRowIndex = extendRow.ParentRow;
        return rootRowIndex == VolumeRootRecordNumber &&
               rootRowIndex < block.RowCount &&
               block.Rows[(int)rootRowIndex].IsInUse &&
               block.Rows[(int)rootRowIndex].ParentRow == rootRowIndex;
    }

    static bool RowNameEquals(BlockFile block, uint rowIndex, ReadOnlySpan<char> expected)
    {
        var name = NamePool.ReadRowName(block, rowIndex);
        if (name.Length != expected.Length)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[expected.Length];
        name.CopyTo(buffer);
        return MemoryExtensions.Equals(buffer, expected, StringComparison.OrdinalIgnoreCase);
    }

    bool TryHydrateRow(UsnJournalEntry entry, uint rowIndex, out bool hydrated)
    {
        if ((Writer.ReadRowFlags(rowIndex) & RowFlags.InUse) != 0)
        {
            hydrated = false;
            return true;
        }

        if (entry.ParentRecordNumber > uint.MaxValue)
        {
            Writer.MarkCapacityExhausted();
            hydrated = false;
            return false;
        }

        var columns = BuildColumns(entry);
        hydrated = true;
        return Writer.TryWriteRow(rowIndex, entry.FileName, in columns);
    }

    static RowColumns BuildColumns(UsnJournalEntry entry)
    {
        var flags = RowFlags.InUse | RowFlags.SizeUnknown | RowFlags.LastWriteTimeUnknown;
        if ((entry.FileAttributes & FileAttributes.Directory) != 0)
        {
            flags |= RowFlags.Directory;
        }

        return new RowColumns((uint)entry.ParentRecordNumber, flags, (uint)entry.FileAttributes, Size: 0,
            ModifiedTicks: 0, entry.SequenceNumber);
    }
}
