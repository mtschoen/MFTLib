namespace MFTLib.Index;

/// <summary>
///     The hard-link arm of the mutator (issue 1008): a row stores one link of its file, and a hard-link record does
///     not say which of the file's links survives, so the row follows the source's re-read of the file's MFT record
///     instead of the record's own name.
/// </summary>
sealed partial class JournalMutator
{
    readonly Dictionary<(uint Row, ushort Sequence), FileNameResolution> _fileNames = [];

    void RememberFileNames(IReadOnlyList<FileNameResolution>? fileNames)
    {
        foreach (var fileName in fileNames ?? [])
        {
            if (fileName.RecordNumber <= uint.MaxValue)
            {
                _fileNames[((uint)fileName.RecordNumber, fileName.SequenceNumber)] = fileName;
            }
        }
    }

    /// <summary>
    ///     The hard-link arm of <see cref="ApplyOne" />, for every record of a cycle that carries
    ///     <see cref="UsnReason.HardLinkChange" />. The row takes the name and parent the source re-read from the file's
    ///     MFT record, the ones a fresh scan gives it. A resolved answer the row does not already show is applied as a
    ///     rename; an unreadable or missing answer detaches the row, because its parent can no longer be verified; a
    ///     superseded answer means the file is gone, which the journal's later records apply. Applying the same answer
    ///     to the cycle's later records changes nothing. Scanned size and last-write observations are kept: a link
    ///     change alters names only.
    /// </summary>
    ApplyOutcome ApplyLinkChange(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification, ref bool rowMutated)
    {
        var lookup = _fileNames.TryGetValue((rowIndex, entry.SequenceNumber), out var answer)
            ? answer
            : FileNameResolution.Unreadable(entry.RecordNumber, entry.SequenceNumber);
        var resolved = lookup.Outcome == FileNameLookupOutcome.Resolved;
        if ((classification & UsnReason.FileCreate) != 0)
        {
            var parent = lookup.Outcome switch
            {
                FileNameLookupOutcome.Resolved => lookup.ParentRecordNumber,
                FileNameLookupOutcome.Unreadable => BlockLayout.DetachedParentRow,
                _ => entry.ParentRecordNumber
            };
            return ApplyCreate(rootBlock, entry, rowIndex, resolved ? lookup.Name : entry.FileName, parent);
        }

        if (IsDeletedRow(rowIndex))
        {
            return ApplyOutcome.Applied(null);
        }

        var hydration = resolved
            ? TryHydrateRow(entry, rowIndex, lookup.Name, lookup.ParentRecordNumber, out var hydrated)
            : TryHydrateRow(entry, rowIndex, out hydrated);
        if (hydration.Refusal != WriteRefusal.None)
        {
            return hydration;
        }

        if (hydrated)
        {
            if (lookup.Outcome == FileNameLookupOutcome.Unreadable)
            {
                Writer.DetachRow(rowIndex);
            }

            return ApplyOutcome.Applied(new FileChange(FileChangeKind.Created,
                FileEntry.Create(Written(rootBlock), rowIndex), IndexNavigation.BuildChangePath(Written(rootBlock), rowIndex),
                entry.TimestampUtc));
        }

        if (resolved && !RowShows(rowIndex, lookup.Name, lookup.ParentRecordNumber))
        {
            return ApplyRename(rootBlock, entry, rowIndex, classification, lookup.Name, lookup.ParentRecordNumber);
        }

        if (lookup.Outcome == FileNameLookupOutcome.Unreadable &&
            Writer.Block.Rows[(int)rowIndex].ParentRow != BlockLayout.DetachedParentRow)
        {
            return Detach(rootBlock, entry, rowIndex, classification);
        }

        if (classification != UsnReason.None)
        {
            return ApplyModification(rootBlock, entry, rowIndex, classification);
        }

        if (entry.IsClose && ApplyCloseMetadata(entry, rowIndex))
        {
            rowMutated = true;
        }

        return ApplyOutcome.Applied(null);
    }

    /// <summary>Detaches a row whose surviving link is unknown; its path becomes its bare name, reported as a rename.</summary>
    ApplyOutcome Detach(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex, UsnReason classification)
    {
        var previousPath = IndexNavigation.BuildChangePath(Written(rootBlock), rowIndex);
        Writer.DetachRow(rowIndex);
        Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification));
        return ApplyOutcome.Applied(new FileChange(FileChangeKind.Renamed,
            FileEntry.Create(Written(rootBlock), rowIndex), IndexNavigation.BuildChangePath(Written(rootBlock), rowIndex),
            entry.TimestampUtc, previousPath));
    }

    /// <summary>True when the row's name and parent are already <paramref name="name" /> and <paramref name="parentRecordNumber" />.</summary>
    bool RowShows(uint rowIndex, string name, ulong parentRecordNumber)
    {
        return Writer.Block.Rows[(int)rowIndex].ParentRow == parentRecordNumber &&
               NamePool.ReadRowName(Writer.Block, rowIndex).EqualsOrdinal(name);
    }
}
