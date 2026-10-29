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
    public JournalMutator(BlockWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Writer = writer;
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

        // The reason bit alone cannot tell a repeated RenameNewName apart from a new
        // rename: NTFS writes one old-name/new-name pair per rename and does not require a
        // close between renames, so one open cycle can hold several real renames. Only the
        // exact repetition of the name and parent this cycle already applied is the close
        // record's (or a cumulative intermediate record's) echo; a different name or
        // parent classifies as the new rename it is.
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
            // The row already holds a newer file reference than this record names, as when a scan imported the
            // later file while the replayed journal still carries the earlier one's delete.
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
            if (entry.IsClose)
            {
                if (ApplyCloseMetadata(entry, rowIndex))
                {
                    rowMutated = true;
                }
            }
        }

        if (entry.IsClose)
        {
            cycles.CloseCycle(rowIndex);
        }
        else if (meaningful != UsnReason.None)
        {
            // Every meaningful bit of a reported record counts as reported, not only the
            // bit that won the classification: the record was reported once, and its
            // remaining bits are subsumed by that one change. The name and parent ride
            // along so the cycle can tell a rename echo from the next real rename.
            cycles.MarkReported(rowIndex, entry.SequenceNumber, meaningful, entry.FileName,
                entry.ParentRecordNumber);
        }

        return change;
    }

    FileChange? ApplyDelete(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex)
    {
        if (IsDeletedRow(rowIndex) || !TryHydrateRow(entry, rowIndex, out _))
        {
            return null;
        }

        var path = IndexNavigation.BuildPath(rootBlock, rowIndex);
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
    ///     A close record that repeats only already-reported reasons still carries the
    ///     freshest attributes for the row, so they are applied without a change notification
    ///     (mutation separated from notification). The record's time is never stored: the
    ///     reasons the cycle already reported have already invalidated whatever they could
    ///     have changed, and a close that reports nothing new leaves the scanned size and
    ///     last-write observations alone. An unused or tombstoned slot stays untouched:
    ///     bookkeeping never hydrates a row or resurrects a deleted file. Name and parent are
    ///     not restamped: they were written by the record that opened the cycle and cannot
    ///     change without a rename record of their own. Returns true if row metadata was modified.
    /// </summary>
    bool ApplyCloseMetadata(UsnJournalEntry entry, uint rowIndex)
    {
        return Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, RowFreshness.Unchanged);
    }

    FileChange? ApplyCreate(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex)
    {
        var columns = BuildColumns(entry);
        // A refused write (the name, the row slot or the posting delta no longer fits) has set the capacity-exhausted
        // flag and written no column: the row keeps its prior shape and the rescan that follows rebuilds the postings.
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
        // A false result means the new name or its posting delta entry did not fit; the row keeps its old name.
        if (!Writer.TryRenameRow(rowIndex, entry.FileName, (uint)entry.ParentRecordNumber))
        {
            // The name did not fit, but the cycle now counts every reason of this record as reported, so a
            // content or basic-information reason must still invalidate the row here or a close record cannot.
            if (Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification)))
            {
                rowMutated = true;
            }

            return null;
        }

        // A rename alone changes neither size nor last-write time, so the scan's observations survive it.
        Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification));
        return new FileChange(FileChangeKind.Renamed,
            FileEntry.Create(rootBlock, rowIndex), IndexNavigation.BuildPath(rootBlock, rowIndex),
            entry.TimestampUtc, previousPath);
    }

    /// <summary>
    ///     Handles any reason other than create, delete, or rename. Updating <c>Attributes</c>
    ///     here is intentional (plan decision 10), not an oversight: a non-create, non-delete,
    ///     non-rename USN reason is exactly the case where attribute metadata can have changed
    ///     without the name or the parent changing. A USN record carries no size and no
    ///     last-write time, so the freshness the reasons imply (<see cref="FreshnessFor" />)
    ///     marks those observations unknown rather than replacing them with the record's own
    ///     time. <see cref="ApplyOne" /> calls this only for a classification that still holds
    ///     a reason other than Close and RenameOldName.
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

    /// <summary>
    ///     What the reasons a record newly reports do to a row's scanned observations. Content
    ///     reasons can change both the size and the last-write time; a basic-information change
    ///     can change the last-write time; every other reason changes neither.
    /// </summary>
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
    ///     A deleted row, whether the journal deleted it or a scan imported it freed, is never modified, renamed,
    ///     deleted again or hydrated live. Only a create reuses its slot, because NTFS reuses record numbers.
    /// </summary>
    /// <summary>
    ///     True when the row holds a file whose sequence number differs from the record's: the record belongs to
    ///     another incarnation of the record number and must not mutate this one. A free or tombstoned row has no
    ///     live file to protect, and a stored zero means the producer recorded no sequence (NTFS never uses zero).
    /// </summary>
    bool IsEarlierIncarnation(uint rowIndex, UsnJournalEntry entry)
    {
        ref readonly var row = ref Writer.Block.Rows[(int)rowIndex];
        var stored = Writer.Block.SequenceNumbers[(int)rowIndex];
        return row.IsInUse && !row.IsDeleted && stored != 0 && stored != entry.SequenceNumber;
    }

    bool IsDeletedRow(uint rowIndex)
    {
        return (Writer.ReadRowFlags(rowIndex) & RowFlags.Tombstone) != 0;
    }

    /// <summary>
    ///     Fills an unwritten slot from the journal entry that just referred to it. Without this,
    ///     a modification produces no change at all and a delete tombstones an empty slot with no
    ///     name to report. <paramref name="hydrated" /> tells the caller whether this call is what
    ///     populated the row, which only the rename arm needs: a rename of a row the index never
    ///     had is reported as a create, not a rename, because there is no real previous path to give it.
    /// </summary>
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

    /// <summary>
    ///     The row one journal entry describes. Shared by the create arm and the hydration arm,
    ///     which write the same row from the same entry, so the two cannot describe it
    ///     differently. A USN record carries neither a size nor a last-write time, so the row is
    ///     written with both unknown and only a producer
    ///     can supply them. The record's own time is never stored in the last-write column.
    /// </summary>
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
