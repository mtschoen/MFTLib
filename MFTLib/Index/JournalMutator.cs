namespace MFTLib.Index;

/// <summary>
///     Applies USN journal batches to a block in place. Handed-out handles never dangle and
///     never read garbage; their values simply become current. A journal record carries only
///     its own event time, never the file's size or last-write time, so the mutator keeps those
///     scanned observations when an event cannot have changed them and marks them unknown when
///     it can have; every row write goes through <see cref="BlockWriter" />, which holds the
///     block's access scope. The batch also holds an access scope across incarnation checks,
///     handle creation and event path reads between writer calls. A record the block cannot store is
///     refused whole: nothing of it is written and its cycle is not marked.
///     <see cref="ApplyUntilRefusal" /> stops there with the cursor on the refused record, and the index either grows
///     the block and resumes at that record or, when no growth can store it, flags the block, skips the record and
///     resumes after it. NTFS closes every open cycle with a record
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
///     Change paths come from <see cref="IndexNavigation.BuildChangePath" />, which never fails on content: a parent
///     chain that cannot be rendered degrades that one change to the bare name, so the batch still commits and the
///     cursor still advances.
/// </summary>
sealed class JournalMutator
{
    internal delegate bool DeletePendingRenameDetector(BlockFile block, ulong newParentRecordNumber, string newName);

    readonly DeletePendingRenameDetector _isDeletePendingRename;

    public JournalMutator(BlockWriter writer, DeletePendingRenameDetector? isDeletePendingRename = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Writer = writer;
        _isDeletePendingRename = isDeletePendingRename ?? ExtendDeletedTarget.Matches;
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
    ///     Applies <paramref name="entries" /> from <paramref name="firstIndex" /> in order and stops at the first record
    ///     the block cannot store. Entries are applied sequentially rather than grouped, because a create and a delete
    ///     for one record can share a batch. A refusal writes nothing of its record and leaves its cycle unmarked, so
    ///     the block is exactly consistent: rows reflect the records before the refused index, and the cursor is
    ///     written as <c>(journalIdentifier, entries[i].Usn)</c> for the refused index <c>i</c>. Without a refusal the
    ///     cursor is <paramref name="nextUsn" />. The generation is bumped once if the applied prefix changed anything.
    /// </summary>
    /// <param name="rootBlock">The root block the changes are minted against.</param>
    /// <param name="entries">The batch.</param>
    /// <param name="firstIndex">Index of the first record to apply, from 0 to the entry count.</param>
    /// <param name="journalIdentifier">Identifier of the journal the cursor belongs to.</param>
    /// <param name="nextUsn">Cursor to store when the whole range applies.</param>
    /// <returns>The applied prefix's changes and the refusal it stopped at, if any.</returns>
    public JournalApplyResult ApplyUntilRefusal(RootBlock rootBlock,
        IReadOnlyList<UsnJournalEntry> entries, int firstIndex, ulong journalIdentifier, long nextUsn)
    {
        ArgumentNullException.ThrowIfNull(rootBlock);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(firstIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstIndex, entries.Count);

        // Writer methods protect only their own accesses. Keep incarnation reads, handle creation
        // and event path materialization mapped across the gaps between those methods as well.
        using var access = Writer.Block.TakeAccess();
        var changes = new List<FileChange>();
        var rowMutated = false;
        JournalRefusal? refusal = null;
        for (var index = firstIndex; index < entries.Count; index++)
        {
            var outcome = ApplyOne(rootBlock, entries[index], ref rowMutated);
            if (outcome.Refusal != WriteRefusal.None)
            {
                refusal = new JournalRefusal { Index = index, Reason = outcome.Refusal, Growable = outcome.Growable };
                break;
            }

            if (outcome.Change is not null)
            {
                changes.Add(outcome.Change);
            }
        }

        Writer.SetJournalCursor(journalIdentifier, refusal is { } stopped ? entries[stopped.Index].Usn : nextUsn);
        if (changes.Count > 0 || rowMutated)
        {
            Writer.BumpGeneration();
        }

        return new JournalApplyResult(changes, refusal);
    }

    /// <summary>The result of one record: the change it minted, or the refusal that left it unapplied.</summary>
    readonly record struct ApplyOutcome(FileChange? Change, WriteRefusal Refusal = WriteRefusal.None,
        bool Growable = false)
    {
        public static ApplyOutcome Applied(FileChange? change) => new(change);

        public static ApplyOutcome Refused(WriteRefusal reason, bool growable = true) => new(null, reason, growable);
    }

    ApplyOutcome ApplyOne(RootBlock rootBlock, UsnJournalEntry entry, ref bool rowMutated)
    {
        var rowIndex = (uint)entry.RecordNumber;
        if (entry.RecordNumber > uint.MaxValue)
        {
            return ApplyOutcome.Refused(WriteRefusal.Slots, growable: false);
        }

        if (rowIndex >= Writer.SlotCapacity)
        {
            return ApplyOutcome.Refused(WriteRefusal.Slots);
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

        ApplyOutcome outcome;
        if ((classification & UsnReason.FileCreate) == 0 && IsEarlierIncarnation(rowIndex, entry))
        {
            outcome = ApplyOutcome.Applied(null);
        }
        else if ((classification & UsnReason.FileCreate) != 0)
        {
            outcome = ApplyCreate(rootBlock, entry, rowIndex);
        }
        else if ((classification & UsnReason.FileDelete) != 0)
        {
            outcome = ApplyDelete(rootBlock, entry, rowIndex);
        }
        else if ((classification & UsnReason.RenameNewName) != 0)
        {
            outcome = ApplyRenameArm(rootBlock, entry, rowIndex, classification);
        }
        else if (classification != UsnReason.None)
        {
            outcome = ApplyModification(rootBlock, entry, rowIndex, classification);
        }
        else
        {
            outcome = ApplyOutcome.Applied(null);
            if (entry.IsClose && ApplyCloseMetadata(entry, rowIndex))
            {
                rowMutated = true;
            }
        }

        // A refused record is applied whole later, so its cycle must not read as reported: a later close record
        // of the same cycle would otherwise be taken for an echo.
        if (outcome.Refusal != WriteRefusal.None)
        {
            return outcome;
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

        return outcome;
    }

    ApplyOutcome ApplyDelete(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex)
    {
        var flags = Writer.ReadRowFlags(rowIndex);
        if ((flags & RowFlags.Tombstone) != 0)
        {
            return ApplyOutcome.Applied(null);
        }

        var hydration = TryHydrateRow(entry, rowIndex, out _);
        if (hydration.Refusal != WriteRefusal.None)
        {
            return hydration;
        }

        // A pending row stores its pre-move full path verbatim in the name column, so completing
        // the tombstone never walks a parent chain that may have been renamed or reused since.
        var path = (flags & RowFlags.DeletePending) != 0
            ? NamePool.ReadRowName(Writer.Block, rowIndex).ToString()
            : IndexNavigation.BuildChangePath(rootBlock, rowIndex);
        var refusal = Writer.MarkTombstone(rowIndex);
        if (refusal != WriteRefusal.None)
        {
            return ApplyOutcome.Refused(refusal);
        }

        return ApplyOutcome.Applied(new FileChange(FileChangeKind.Deleted, FileEntry.Create(rootBlock, rowIndex),
            path, entry.TimestampUtc));
    }

    /// <summary>
    ///     The rename arm of <see cref="ApplyOne" />: a rename of a row the index never
    ///     had is reported as a create, because there is no real previous path to give it.
    /// </summary>
    ApplyOutcome ApplyRenameArm(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification)
    {
        if (IsDeletedRow(rowIndex))
        {
            return ApplyOutcome.Applied(null);
        }

        var hydration = TryHydrateRow(entry, rowIndex, out var hydrated);
        if (hydration.Refusal != WriteRefusal.None)
        {
            return hydration;
        }

        if (hydrated)
        {
            return ApplyOutcome.Applied(new FileChange(FileChangeKind.Created,
                FileEntry.Create(rootBlock, rowIndex), IndexNavigation.BuildChangePath(rootBlock, rowIndex),
                entry.TimestampUtc));
        }

        return ApplyRename(rootBlock, entry, rowIndex, classification);
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

    ApplyOutcome ApplyCreate(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex)
    {
        if (entry.ParentRecordNumber > uint.MaxValue)
        {
            return ApplyOutcome.Refused(WriteRefusal.Slots, growable: false);
        }

        var columns = BuildColumns(entry);
        var refusal = Writer.TryWriteRow(rowIndex, entry.FileName, in columns);
        if (refusal != WriteRefusal.None)
        {
            return ApplyOutcome.Refused(refusal);
        }

        Writer.InvalidateDeletedDescendants(rowIndex);

        return ApplyOutcome.Applied(new FileChange(FileChangeKind.Created, FileEntry.Create(rootBlock, rowIndex),
            IndexNavigation.BuildChangePath(rootBlock, rowIndex), entry.TimestampUtc));
    }

    ApplyOutcome ApplyRename(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification)
    {
        if (entry.ParentRecordNumber > uint.MaxValue)
        {
            return ApplyOutcome.Refused(WriteRefusal.Slots, growable: false);
        }

        var previousPath = IndexNavigation.BuildChangePath(rootBlock, rowIndex);
        if (_isDeletePendingRename(Writer.Block, entry.ParentRecordNumber, entry.FileName))
        {
            var pendingRefusal = Writer.MarkDeletePending(rowIndex, previousPath);
            return pendingRefusal != WriteRefusal.None
                ? ApplyOutcome.Refused(pendingRefusal)
                : ApplyOutcome.Applied(new FileChange(FileChangeKind.DeletePending,
                    FileEntry.Create(rootBlock, rowIndex), previousPath, entry.TimestampUtc));
        }

        // A refused rename leaves the row whole: the record's attributes are not applied without its name.
        var renameRefusal = Writer.TryRenameRow(rowIndex, entry.FileName, (uint)entry.ParentRecordNumber);
        if (renameRefusal != WriteRefusal.None)
        {
            return ApplyOutcome.Refused(renameRefusal);
        }

        Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification));
        return ApplyOutcome.Applied(new FileChange(FileChangeKind.Renamed,
            FileEntry.Create(rootBlock, rowIndex), IndexNavigation.BuildChangePath(rootBlock, rowIndex),
            entry.TimestampUtc, previousPath));
    }

    /// <summary>
    ///     Handles any reason other than create, delete, or rename, updating attributes and
    ///     invalidating scanned observations per <see cref="FreshnessFor" />.
    /// </summary>
    ApplyOutcome ApplyModification(RootBlock rootBlock, UsnJournalEntry entry, uint rowIndex,
        UsnReason classification)
    {
        if (IsDeletedRow(rowIndex))
        {
            return ApplyOutcome.Applied(null);
        }

        var hydration = TryHydrateRow(entry, rowIndex, out _);
        if (hydration.Refusal != WriteRefusal.None)
        {
            return hydration;
        }

        Writer.UpdateRowMetadata(rowIndex, (uint)entry.FileAttributes, FreshnessFor(classification));
        Hooks._beforeModificationPath?.Invoke();
        return ApplyOutcome.Applied(new FileChange(FileChangeKind.Modified, FileEntry.Create(rootBlock, rowIndex),
            IndexNavigation.BuildChangePath(rootBlock, rowIndex), entry.TimestampUtc));
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
    ///     Writes the row of a record whose row the index never had. A refusal is returned as the outcome and writes
    ///     nothing; otherwise the outcome is applied with no change and <paramref name="hydrated" /> says whether a row
    ///     was written.
    /// </summary>
    ApplyOutcome TryHydrateRow(UsnJournalEntry entry, uint rowIndex, out bool hydrated)
    {
        hydrated = false;
        if ((Writer.ReadRowFlags(rowIndex) & RowFlags.InUse) != 0)
        {
            return ApplyOutcome.Applied(null);
        }

        if (entry.ParentRecordNumber > uint.MaxValue)
        {
            return ApplyOutcome.Refused(WriteRefusal.Slots, growable: false);
        }

        var columns = BuildColumns(entry);
        var refusal = Writer.TryWriteRow(rowIndex, entry.FileName, in columns);
        hydrated = refusal == WriteRefusal.None;
        return refusal == WriteRefusal.None ? ApplyOutcome.Applied(null) : ApplyOutcome.Refused(refusal);
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
