using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A row a scan imported as a freed record is deleted for the life of the block: the journal never edits,
///     renames, deletes again or resurrects it, and only a create reuses its slot.
/// </summary>
[TestClass]
public class JournalMutatorFreedRowTests
{
    const uint FreedRow = 28;

    [DataTestMethod]
    [DataRow((uint)UsnReason.DataOverwrite, DisplayName = "modify")]
    [DataRow((uint)(UsnReason.DataOverwrite | UsnReason.Close), DisplayName = "modify and close")]
    [DataRow((uint)UsnReason.RenameNewName, DisplayName = "rename")]
    [DataRow((uint)UsnReason.FileDelete, DisplayName = "delete")]
    [DataRow((uint)UsnReason.BasicInfoChange, DisplayName = "metadata")]
    public async Task EventForAFreedRecord_ChangesNothingAndDoesNotResurrectIt(uint reason)
    {
        await using var fixture = new MutatorFixture();
        WriteFreedRow(fixture);
        var liveRowsBefore = fixture.Block.Header.LiveRowCount;
        var modifiedBefore = fixture.Block.Rows[(int)FreedRow].ModifiedTicks;

        var changes = fixture.Apply([Entry((UsnReason)reason, "renamed-away.txt", parent: 7)]);

        Assert.AreEqual(0, changes.Count);
        ref var row = ref fixture.Block.Rows[(int)FreedRow];
        Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, row.Flags);
        Assert.AreEqual("freed.txt", NamePool.ReadRowName(fixture.Block, FreedRow).ToString());
        Assert.AreEqual(6u, row.ParentRow);
        Assert.AreEqual(modifiedBefore, row.ModifiedTicks);
        Assert.AreEqual(liveRowsBefore, fixture.Block.Header.LiveRowCount);
    }

    [TestMethod]
    public async Task EventForADetachedFreedRecord_LeavesItDetached()
    {
        await using var fixture = new MutatorFixture();
        WriteFreedRow(fixture, parentSequence: 99);

        var changes = fixture.Apply([Entry(UsnReason.RenameNewName, "elsewhere.txt", parent: 6)]);

        Assert.AreEqual(0, changes.Count);
        Assert.AreEqual(BlockLayout.DetachedParentRow, fixture.Block.Rows[(int)FreedRow].ParentRow);
        Assert.AreEqual("freed.txt", NamePool.ReadRowName(fixture.Block, FreedRow).ToString());
    }

    const uint DirectChildRow = 29;
    const uint DeeperChildRow = 30;
    const uint DocumentsRow = 6;
    const uint NotesRow = 7;

    [DataTestMethod]
    [DataRow(true, (ushort)15, "unrelated-dir", DisplayName = "directory replacement, new sequence")]
    [DataRow(false, (ushort)14, "next-file.txt", DisplayName = "file replacement, equal sequence")]
    public async Task Create_ReusesAFreedRecordsSlotAsALiveRow(bool asDirectory, ushort replacementSequence,
        string replacementName)
    {
        await using var fixture = new MutatorFixture();
        WriteFreedHierarchy(fixture);
        var liveRowsBefore = fixture.Block.Header.LiveRowCount;
        var attributes = asDirectory ? FileAttributes.Directory : FileAttributes.Archive;

        var change = fixture.Apply([Entry(UsnReason.FileCreate, replacementName, parent: 6,
            sequence: replacementSequence, attributes: attributes)]).Single();

        Assert.AreEqual(FileChangeKind.Created, change.Kind);
        Assert.IsFalse(fixture.Block.Rows[(int)FreedRow].IsDeleted);
        Assert.AreEqual(asDirectory, fixture.Block.Rows[(int)FreedRow].IsDirectory);
        Assert.AreEqual(replacementName, NamePool.ReadRowName(fixture.Block, FreedRow).ToString());
        Assert.AreEqual(replacementSequence, fixture.Block.SequenceNumbers[(int)FreedRow]);
        Assert.AreEqual(liveRowsBefore + 1, fixture.Block.Header.LiveRowCount);

        Assert.AreEqual(BlockLayout.DetachedParentRow, fixture.Block.Rows[(int)DirectChildRow].ParentRow);
        var directChild = FileEntry.Create(fixture.Snapshot, 0, DirectChildRow);
        Assert.AreEqual("child-dir", directChild.Path);
        Assert.IsNull(directChild.Parent);

        Assert.AreEqual(BlockLayout.DetachedParentRow, fixture.Block.Rows[(int)DeeperChildRow].ParentRow);
        var deeperChild = FileEntry.Create(fixture.Snapshot, 0, DeeperChildRow);
        Assert.AreEqual("deeper.txt", deeperChild.Path);
        Assert.IsNull(deeperChild.Parent);

        var replacement = FileEntry.Create(fixture.Snapshot, 0, FreedRow);
        var deletedSubtreeSearch = SearchEngine.Search(fixture.Snapshot,
            new SearchQuery(null, Under: replacement, IncludeDeleted: true))
            .Where(entry => entry.IsDeleted)
            .ToList();
        Assert.AreEqual(0, deletedSubtreeSearch.Count);

        var deletedSubtreeEnumerate = SearchEngine.Enumerate(fixture.Snapshot,
            new SearchQuery(null, Under: replacement, IncludeDeleted: true))
            .Where(entry => entry.IsDeleted)
            .ToList();
        Assert.AreEqual(0, deletedSubtreeEnumerate.Count);

        // The invalidation visits exactly the reused slot's deleted descendants (child-dir and
        // deeper.txt), never the whole block: work scales with the affected subtree, not with the
        // row count. A null links index would mean invalidation never ran, which -1 fails too.
        Assert.AreEqual(2L, fixture.Block.DeletedChildren?._descendantsVisitedForTest ?? -1L);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "no deleted rows in the block")]
    [DataRow(true, DisplayName = "a deleted row under another parent")]
    public async Task Create_OnAFreshSlotVisitsNoDeletedDescendants(bool deleteRowFirst)
    {
        await using var fixture = new MutatorFixture();
        if (deleteRowFirst)
        {
            fixture.Apply([EntryAt(NotesRow, UsnReason.FileDelete, "notes.txt", parent: 6, sequence: 4)]);
        }

        const uint firstFreshRow = 40;
        for (var i = 0u; i < 3; i++)
        {
            var change = fixture.Apply([EntryAt(firstFreshRow + i, UsnReason.FileCreate, $"fresh{i}.txt",
                parent: 6, sequence: (ushort)(5 + i))]).Single();
            Assert.AreEqual(FileChangeKind.Created, change.Kind);
        }

        // A fresh slot has no deleted descendants, so invalidation visits nothing no matter how
        // many rows the block holds; the full-row scan this replaced visited every row for every
        // create. A null links index would mean invalidation never ran, which -1 fails too.
        Assert.AreEqual(0L, fixture.Block.DeletedChildren?._descendantsVisitedForTest ?? -1L);
        if (deleteRowFirst)
        {
            Assert.IsTrue(fixture.Block.Rows[(int)NotesRow].IsDeleted);
            Assert.AreEqual(DocumentsRow, fixture.Block.Rows[(int)NotesRow].ParentRow);
        }
    }

    [TestMethod]
    public async Task Create_DeleteAndReuseAfterTheLinksBuildKeepThemCurrent()
    {
        await using var fixture = new MutatorFixture();

        // The first create builds the reverse links from the rows as written.
        fixture.Apply([EntryAt(40, UsnReason.FileCreate, "fresh.txt", parent: 6, sequence: 4)]);

        // A delete after the build links the tombstoned row under its parent.
        fixture.Apply([EntryAt(NotesRow, UsnReason.FileDelete, "notes.txt", parent: 6, sequence: 5)]);

        // Reusing the slot unlinks the row again: the row is live under documents (6) from here on.
        fixture.Apply([EntryAt(NotesRow, UsnReason.FileCreate, "reused.txt", parent: 6, sequence: 6)]);

        // Reusing the parent slot must not reach the live row through a stale link.
        fixture.Apply([EntryAt(DocumentsRow, UsnReason.FileDelete, "documents", parent: 5, sequence: 7)]);
        fixture.Apply([EntryAt(DocumentsRow, UsnReason.FileCreate, "newdir", parent: 5, sequence: 8,
            attributes: FileAttributes.Directory)]);
        Assert.AreEqual(DocumentsRow, fixture.Block.Rows[(int)NotesRow].ParentRow);
        Assert.IsFalse(fixture.Block.Rows[(int)NotesRow].IsDeleted);

        // A direct rename of a deleted row relinks it under its new parent...
        fixture.Apply([EntryAt(40, UsnReason.FileDelete, "fresh.txt", parent: 6, sequence: 9)]);
        var writer = new BlockWriter(fixture.Block);
        Assert.IsTrue(writer.TryRenameRow(40, "moved.txt", parentRow: 5));

        // ...so reusing the old parent does not detach it through a stale link either.
        fixture.Apply([EntryAt(DocumentsRow, UsnReason.FileDelete, "newdir", parent: 5, sequence: 10)]);
        fixture.Apply([EntryAt(DocumentsRow, UsnReason.FileCreate, "newdir2", parent: 5, sequence: 11,
            attributes: FileAttributes.Directory)]);
        Assert.AreEqual(5u, fixture.Block.Rows[40].ParentRow);

        // A direct detach unlinks the row from its parent.
        writer.DetachRow(40);
        Assert.AreEqual(BlockLayout.DetachedParentRow, fixture.Block.Rows[40].ParentRow);

        // Nothing above ever had a deleted descendant to detach.
        Assert.AreEqual(0L, fixture.Block.DeletedChildren?._descendantsVisitedForTest ?? -1L);
    }

    static UsnJournalEntry EntryAt(uint record, UsnReason reason, string name, ulong parent,
        ushort sequence, FileAttributes attributes = FileAttributes.Archive) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason,
            FileAttributes = attributes,
            SequenceNumber = sequence,
            Usn = 1000,
            TimestampUtc = new DateTime(2026, 10, 6, 6, 0, 0, DateTimeKind.Utc)
        });

    // Root 5 -> Documents 6 (live directory, sequence 0) -> FreedRow 28 (freed directory)
    // -> DirectChildRow 29 (freed directory) -> DeeperChildRow 30 (freed file).
    static void WriteFreedHierarchy(MutatorFixture fixture)
    {
        var freedDirectory = new MftRecord(FreedRow, 6,
            new MftRecordFields(2, FileAttributes.Directory, 0, 0, 14), "freed-dir");
        var directChild = new MftRecord(DirectChildRow, FreedRow,
            new MftRecordFields(2, FileAttributes.Directory, 0, 0, 14, 13), "child-dir");
        var deeperChild = new MftRecord(DeeperChildRow, DirectChildRow,
            new MftRecordFields(0, FileAttributes.Archive, 10, 0, 14, 13), "deeper.txt");

        MftBlockRowWriter.WriteBatches(new BlockWriter(fixture.Block),
            [[freedDirectory, directChild, deeperChild]],
            new MftBlockRowFilter(IncludeFreed: true), null, CancellationToken.None);
    }

    // The freed record hangs under "documents" (row 6) and verifies against it unless parentSequence says otherwise.
    static void WriteFreedRow(MutatorFixture fixture, ushort parentSequence = 0)
    {
        var freed = new MftRecord(FreedRow, 6, new MftRecordFields(0, FileAttributes.Archive, 10, 0, 4,
            parentSequence), "freed.txt");
        MftBlockRowWriter.WriteBatches(new BlockWriter(fixture.Block), [[freed]],
            new MftBlockRowFilter(IncludeFreed: true), null, CancellationToken.None);
    }

    static UsnJournalEntry Entry(UsnReason reason, string name, ulong parent, ushort sequence = 4,
        FileAttributes attributes = FileAttributes.Archive) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = FreedRow,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason,
            FileAttributes = attributes,
            SequenceNumber = sequence,
            Usn = 1000,
            TimestampUtc = new DateTime(2026, 10, 6, 6, 0, 0, DateTimeKind.Utc)
        });
}
