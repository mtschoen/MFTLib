namespace MFTLib.Index;

/// <summary>
///     Applies USN journal batches to a block in place. Handed-out handles never dangle and
///     never read garbage; their values simply become current. Capacity exhaustion sets the
///     compaction-needed flag, keeps applying what fits, and reports the drive as stale rather
///     than crashing or silently dropping a record. NTFS closes every open cycle with a record
///     that repeats the cycle's reasons plus <see cref="UsnReason.Close" />; the mutator
///     coalesces that close record against the reasons the cycle already reported (tracked per
///     drive block in <see cref="ReportedReasonCycles" />), so one real transition raises one
///     change while the row still takes the close record's timestamp and attributes.
///     Suppression is keyed on what the cycle applied, not on the reason bit alone: a second
///     rename inside one open cycle (NTFS writes one record pair per rename and requires no
///     close between renames) carries a new name or parent and classifies again; only the
///     repetition of the applied name and parent is the echo.
/// </summary>
public sealed class JournalMutator
{
    public JournalMutator(BlockWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Writer = writer;
    }

    public BlockWriter Writer { get; }

    public bool CompactionNeeded => Writer.CompactionNeeded;

    /// <summary>
    ///     Applies one batch in order, writes the USN cursor into the header, and bumps the
    ///     generation once if anything actually changed. Entries are applied sequentially rather
    ///     than grouped, because a create and a delete for one record can share a batch.
    /// </summary>
    public IReadOnlyList<FileChange> Apply(Snapshot snapshot, ushort driveOrdinal,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(entries);

        var changes = new List<FileChange>();
        var rowMutated = false;
        foreach (var entry in entries)
        {
            var change = ApplyOne(snapshot, driveOrdinal, entry, ref rowMutated);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        Writer.SetJournalCursor(journalId, nextUsn);
        if (changes.Count > 0 || rowMutated)
        {
            Writer.BumpGeneration();
        }

        return changes;
    }

    FileChange? ApplyOne(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, ref bool rowMutated)
    {
        var rowIndex = (uint)entry.RecordNumber;
        if (entry.RecordNumber > uint.MaxValue || rowIndex >= Writer.Block.Header.SlotCapacity)
        {
            Writer.MarkCompactionNeeded();
            return null;
        }

        // NTFS accumulates reason flags while a file handle is open and closes it with a final
        // record that repeats every reason already delivered for that open cycle plus
        // USN_REASON_CLOSE. Every record in the cycle (intermediate or close) is therefore
        // classified only by the reasons this cycle has not reported yet; bits already reported
        // must not classify a subsequent record a second time. RenameOldName never classifies:
        // it is the paired frame whose RenameNewName sibling carries the rename.
        var cycles = snapshot.GetDriveBlock(driveOrdinal).ReportedCycles;
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
        if ((classification & UsnReason.FileCreate) != 0)
        {
            change = ApplyCreate(snapshot, driveOrdinal, entry, rowIndex);
        }
        else if ((classification & UsnReason.FileDelete) != 0)
        {
            change = ApplyDelete(snapshot, driveOrdinal, entry, rowIndex);
        }
        else if ((classification & UsnReason.RenameNewName) != 0)
        {
            change = ApplyRenameArm(snapshot, driveOrdinal, entry, rowIndex);
        }
        else if (classification != UsnReason.None)
        {
            change = ApplyModification(snapshot, driveOrdinal, entry, rowIndex);
        }
        else
        {
            change = null;
            if (entry.IsClose && meaningful != UsnReason.None)
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

    FileChange? ApplyDelete(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        if (!TryHydrateRow(entry, rowIndex, out _))
        {
            return null;
        }

        var path = IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex);
        Writer.MarkTombstone(rowIndex);
        return new FileChange(FileChangeKind.Deleted, FileEntry.Create(snapshot, driveOrdinal, rowIndex), path,
            entry.Timestamp);
    }

    /// <summary>
    ///     The rename arm of <see cref="ApplyOne" />: a rename of a row the index never
    ///     had is reported as a create, because there is no real previous path to give it.
    /// </summary>
    FileChange? ApplyRenameArm(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        if (!TryHydrateRow(entry, rowIndex, out var hydrated))
        {
            return null;
        }

        if (hydrated)
        {
            return new FileChange(FileChangeKind.Created, FileEntry.Create(snapshot, driveOrdinal, rowIndex),
                IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex), entry.Timestamp);
        }

        return ApplyRename(snapshot, driveOrdinal, entry, rowIndex);
    }

    /// <summary>
    ///     A close record that repeats only already-reported reasons still carries the
    ///     freshest metadata for the row, so its timestamp and attributes are applied
    ///     without a change notification (mutation separated from notification). An
    ///     unused or tombstoned slot stays untouched: bookkeeping never hydrates a row
    ///     or resurrects a deleted file. Name and parent are not restamped: they were
    ///     written by the record that opened the cycle and cannot change without a rename
    ///     record of their own. Returns true if row metadata was modified.
    /// </summary>
    bool ApplyCloseMetadata(UsnJournalEntry entry, uint rowIndex)
    {
        ref var row = ref Writer.Block.Rows[(int)rowIndex];
        if (!row.IsInUse || row.IsDeleted)
        {
            return false;
        }

        var modifiedTicks = entry.Timestamp.Ticks;
        var attributes = (uint)entry.FileAttributes;
        if (row.ModifiedTicks == modifiedTicks && row.Attributes == attributes)
        {
            return false;
        }

        row.ModifiedTicks = modifiedTicks;
        row.Attributes = attributes;
        return true;
    }

    FileChange? ApplyCreate(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        var columns = BuildColumns(entry);
        if (!Writer.TryWriteRow(rowIndex, entry.FileName, in columns))
        {
            return null;
        }

        return new FileChange(FileChangeKind.Created, FileEntry.Create(snapshot, driveOrdinal, rowIndex),
            IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex), entry.Timestamp);
    }

    FileChange? ApplyRename(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        var previousPath = IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex);
        if (!Writer.TryRenameRow(rowIndex, entry.FileName, (uint)entry.ParentRecordNumber))
        {
            return null;
        }

        Writer.Block.Rows[(int)rowIndex].ModifiedTicks = entry.Timestamp.Ticks;
        return new FileChange(FileChangeKind.Renamed,
            FileEntry.Create(snapshot, driveOrdinal, rowIndex), IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex),
            entry.Timestamp, previousPath);
    }

    /// <summary>
    ///     Handles any reason other than create, delete, or rename. Updating <c>Attributes</c>
    ///     alongside <c>ModifiedTicks</c> here is intentional (plan decision 10), not an
    ///     oversight: a non-create, non-delete, non-rename USN reason is exactly the case where
    ///     attribute metadata can have changed without the name or the parent changing.
    /// </summary>
    FileChange? ApplyModification(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        // Close on its own is bookkeeping, not a content change, and RenameOldName is the
        // paired frame whose RenameNewName sibling already carries the rename.
        var meaningful = entry.Reason & ~(UsnReason.Close | UsnReason.RenameOldName);
        if (meaningful == UsnReason.None)
        {
            return null;
        }

        if (!TryHydrateRow(entry, rowIndex, out _))
        {
            return null;
        }

        ref var row = ref Writer.Block.Rows[(int)rowIndex];
        // USN records carry no size, so the size column is left to a producer.
        row.ModifiedTicks = entry.Timestamp.Ticks;
        row.Attributes = (uint)entry.FileAttributes;
        return new FileChange(FileChangeKind.Modified, FileEntry.Create(snapshot, driveOrdinal, rowIndex),
            IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex), entry.Timestamp);
    }

    /// <summary>
    ///     Fills a slot a producer filter never wrote, from the journal entry that just referred
    ///     to it. A producer that keeps directories and caller-named files only leaves an ordinary
    ///     file inside a watched tree with no row until it is observed; without this, a
    ///     modification to it produces no change at all and a delete tombstones an empty slot with
    ///     no name to report. <paramref name="hydrated" /> tells the caller whether
    ///     this call is what populated the row, which only the rename arm needs: a rename of a row
    ///     the index never had is reported as a create, not a rename, because there is no real
    ///     previous path to give it.
    /// </summary>
    bool TryHydrateRow(UsnJournalEntry entry, uint rowIndex, out bool hydrated)
    {
        ref var row = ref Writer.Block.Rows[(int)rowIndex];
        if (row.IsInUse)
        {
            hydrated = false;
            return true;
        }

        if (entry.ParentRecordNumber > uint.MaxValue)
        {
            Writer.MarkCompactionNeeded();
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
    ///     differently. The size column is left at zero for both, because a USN record carries no
    ///     size and only a producer can supply one.
    /// </summary>
    static RowColumns BuildColumns(UsnJournalEntry entry)
    {
        var flags = RowFlags.InUse;
        if ((entry.FileAttributes & FileAttributes.Directory) != 0)
        {
            flags |= RowFlags.Directory;
        }
        else
        {
            flags |= RowFlags.SizeUnknown;
        }

        return new RowColumns((uint)entry.ParentRecordNumber, flags, (uint)entry.FileAttributes, Size: 0,
            entry.Timestamp.Ticks, entry.SequenceNumber);
    }
}
