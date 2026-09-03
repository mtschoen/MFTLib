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
            Writer.MarkTombstone(rowIndex);
            return new FileChange(FileChangeKind.Deleted, FileEntry.Create(snapshot, driveOrdinal, rowIndex));
        }

        if ((entry.Reason & UsnReason.RenameNewName) != 0)
        {
            return ApplyRename(snapshot, driveOrdinal, entry, rowIndex);
        }

        return ApplyModification(snapshot, driveOrdinal, entry, rowIndex);
    }

    FileChange? ApplyCreate(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
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

        var columns = new RowColumns((uint)entry.ParentRecordNumber, flags,
            (uint)entry.FileAttributes, Size: 0, entry.Timestamp.Ticks);
        if (!Writer.TryWriteRow(rowIndex, entry.FileName, columns))
        {
            return null;
        }

        return new FileChange(FileChangeKind.Created, FileEntry.Create(snapshot, driveOrdinal, rowIndex));
    }

    FileChange? ApplyRename(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
    {
        var previousName = new string(NamePool.ReadRowName(Writer.Block, rowIndex));
        if (!Writer.TryRenameRow(rowIndex, entry.FileName, (uint)entry.ParentRecordNumber))
        {
            return null;
        }

        Writer.Block.Rows[(int)rowIndex].ModifiedTicks = entry.Timestamp.Ticks;
        return new FileChange(FileChangeKind.Renamed,
            FileEntry.Create(snapshot, driveOrdinal, rowIndex), previousName);
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

        ref var row = ref Writer.Block.Rows[(int)rowIndex];
        if (!row.IsInUse)
        {
            return null;
        }

        // USN records carry no size, so the size column is left to a producer.
        row.ModifiedTicks = entry.Timestamp.Ticks;
        row.Attributes = (uint)entry.FileAttributes;
        return new FileChange(FileChangeKind.Modified, FileEntry.Create(snapshot, driveOrdinal, rowIndex));
    }
}
