namespace MFTLib.Index;

/// <summary>
///     Applies USN journal batches to a block in place. Handed-out handles never dangle and
///     never read garbage; their values simply become current. Capacity exhaustion sets the
///     compaction-needed flag, keeps applying what fits, and reports the drive as stale rather
///     than crashing or silently dropping a record.
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
        foreach (var entry in entries)
        {
            var change = ApplyOne(snapshot, driveOrdinal, entry);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        Writer.SetJournalCursor(journalId, nextUsn);
        if (changes.Count > 0)
        {
            Writer.BumpGeneration();
        }

        return changes;
    }

    FileChange? ApplyOne(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry)
    {
        var rowIndex = (uint)entry.RecordNumber;
        if (entry.RecordNumber > uint.MaxValue || rowIndex >= Writer.Block.Header.SlotCapacity)
        {
            Writer.MarkCompactionNeeded();
            return null;
        }

        if (entry.IsCreate)
        {
            return ApplyCreate(snapshot, driveOrdinal, entry, rowIndex);
        }

        if (entry.IsDelete)
        {
            if (!TryHydrateRow(entry, rowIndex, out _))
            {
                return null;
            }

            var path = IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex);
            Writer.MarkTombstone(rowIndex);
            return new FileChange(FileChangeKind.Deleted, FileEntry.Create(snapshot, driveOrdinal, rowIndex), path);
        }

        if ((entry.Reason & UsnReason.RenameNewName) != 0)
        {
            if (!TryHydrateRow(entry, rowIndex, out var hydrated))
            {
                return null;
            }

            if (hydrated)
            {
                return new FileChange(FileChangeKind.Created, FileEntry.Create(snapshot, driveOrdinal, rowIndex),
                    IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex));
            }

            return ApplyRename(snapshot, driveOrdinal, entry, rowIndex);
        }

        return ApplyModification(snapshot, driveOrdinal, entry, rowIndex);
    }

    FileChange? ApplyCreate(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        var columns = BuildColumns(entry);
        if (!Writer.TryWriteRow(rowIndex, entry.FileName, in columns))
        {
            return null;
        }

        return new FileChange(FileChangeKind.Created, FileEntry.Create(snapshot, driveOrdinal, rowIndex),
            IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex));
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
            previousPath);
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
            IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex));
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
