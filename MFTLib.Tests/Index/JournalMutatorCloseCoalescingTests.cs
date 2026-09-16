using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     NTFS writes one USN record when a reason bit lands and a final record at handle
///     close that repeats the cycle's reasons plus <see cref="UsnReason.Close" />. The
///     mutator reports the first record and coalesces the close record: its metadata is
///     applied to the row, but a reason the cycle already reported produces no second
///     change. Tracked per drive block across batches, reset by a sequence-number change
///     (MFT segment reuse) and by each close record.
/// </summary>
[TestClass]
public class JournalMutatorCloseCoalescingTests
{
    static UsnJournalEntry Entry(ulong recordNumber, ulong parentRecordNumber, string fileName,
        UsnReason reason, DateTime timestamp, ushort sequenceNumber,
        FileAttributes fileAttributes = FileAttributes.Archive)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            SequenceNumber = sequenceNumber,
            Usn = 1000,
            Timestamp = timestamp,
            Reason = reason,
            FileAttributes = fileAttributes,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task CreateThenCreateClose_AcrossBatches_ReportsOneCreatedAndAppliesCloseMetadata()
    {
        await using var fixture = new MutatorFixture();
        var closeMoment = fixture.Timestamp.AddMinutes(1);

        var first = fixture.Apply([Entry(9, 6, "fresh.txt", UsnReason.FileCreate,
            fixture.Timestamp, sequenceNumber: 3)]);
        var generationAfterCreate = fixture.Block.Header.Generation;

        var second = fixture.Apply([Entry(9, 6, "fresh.txt", UsnReason.FileCreate | UsnReason.Close,
            closeMoment, sequenceNumber: 3, FileAttributes.Archive | FileAttributes.ReadOnly)]);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(FileChangeKind.Created, first[0].Kind);
        Assert.AreEqual(0, second.Count);
        // The close record's metadata still lands on the row and advances generation.
        Assert.AreEqual(closeMoment.Ticks, fixture.Block.Rows[9].ModifiedTicks);
        Assert.AreEqual((uint)(FileAttributes.Archive | FileAttributes.ReadOnly),
            fixture.Block.Rows[9].Attributes);
        Assert.AreEqual(generationAfterCreate + 1, fixture.Block.Header.Generation,
            "Close-only batch that mutates row metadata must advance Generation.");
    }

    [TestMethod]
    public async Task DataExtendThenDataExtendClose_AcrossBatches_ReportsOneModified()
    {
        await using var fixture = new MutatorFixture();
        var closeMoment = fixture.Timestamp.AddMinutes(1);

        var first = fixture.Apply([Entry(7, 6, "notes.txt", UsnReason.DataExtend,
            fixture.Timestamp, sequenceNumber: 1)]);
        var second = fixture.Apply([Entry(7, 6, "notes.txt", UsnReason.DataExtend | UsnReason.Close,
            closeMoment, sequenceNumber: 1)]);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(FileChangeKind.Modified, first[0].Kind);
        Assert.AreEqual(0, second.Count);
        Assert.AreEqual(closeMoment.Ticks, fixture.Block.Rows[7].ModifiedTicks);
        Assert.AreEqual(99L, fixture.Block.Rows[7].Size);
    }

    [TestMethod]
    public async Task CloseCarryingAnUnreportedReason_StillReportsOneChange()
    {
        await using var fixture = new MutatorFixture();
        fixture.Apply([Entry(9, 6, "fresh.txt", UsnReason.FileCreate,
            fixture.Timestamp, sequenceNumber: 3)]);

        var second = fixture.Apply([Entry(9, 6, "fresh.txt",
            UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close,
            fixture.Timestamp.AddMinutes(1), sequenceNumber: 3)]);

        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(FileChangeKind.Modified, second[0].Kind);
    }

    [TestMethod]
    public async Task CreateWriteCloseCycle_InOneBatch_ReportsOneCreatedAndOneModified()
    {
        await using var fixture = new MutatorFixture();
        var closeMoment = fixture.Timestamp.AddMinutes(1);

        var changes = fixture.Apply(
        [
            Entry(9, 6, "fresh.txt", UsnReason.FileCreate, fixture.Timestamp, sequenceNumber: 3),
            Entry(9, 6, "fresh.txt", UsnReason.FileCreate | UsnReason.DataExtend, fixture.Timestamp, sequenceNumber: 3),
            Entry(9, 6, "fresh.txt", UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close,
                closeMoment, sequenceNumber: 3)
        ]);

        CollectionAssert.AreEqual(new[] { FileChangeKind.Created, FileChangeKind.Modified },
            changes.Select(change => change.Kind).ToArray());
        Assert.AreEqual(closeMoment.Ticks, fixture.Block.Rows[9].ModifiedTicks);
    }

    [TestMethod]
    public async Task CumulativeReasons_AcrossBatches_ReportsOneCreatedOneModifiedAndAdvancesGeneration()
    {
        await using var fixture = new MutatorFixture();
        var initialGeneration = fixture.Block.Header.Generation;
        var createMoment = fixture.Timestamp;
        var extendMoment = fixture.Timestamp.AddSeconds(10);
        var closeMoment = fixture.Timestamp.AddMinutes(1);

        // Batch 1: FileCreate (file handle opened and file created)
        var first = fixture.Apply([Entry(9, 6, "fresh.txt", UsnReason.FileCreate,
            createMoment, sequenceNumber: 3)]);
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(FileChangeKind.Created, first[0].Kind);
        var generationAfterCreate = fixture.Block.Header.Generation;
        Assert.IsTrue(generationAfterCreate > initialGeneration);

        // Batch 2: FileCreate | DataExtend (intermediate record with accumulated NTFS reason)
        var second = fixture.Apply([Entry(9, 6, "fresh.txt",
            UsnReason.FileCreate | UsnReason.DataExtend, extendMoment, sequenceNumber: 3)]);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(FileChangeKind.Modified, second[0].Kind,
            "Intermediate cumulative record must be classified as Modified, not a duplicate Created.");
        var generationAfterExtend = fixture.Block.Header.Generation;
        Assert.IsTrue(generationAfterExtend > generationAfterCreate);

        // Batch 3: FileCreate | DataExtend | Close (close record repeating all accumulated reasons)
        var third = fixture.Apply([Entry(9, 6, "fresh.txt",
            UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close,
            closeMoment, sequenceNumber: 3, FileAttributes.Archive | FileAttributes.ReadOnly)]);
        Assert.AreEqual(0, third.Count, "Close record must not emit duplicate change.");
        Assert.AreEqual(closeMoment.Ticks, fixture.Block.Rows[9].ModifiedTicks);
        Assert.AreEqual((uint)(FileAttributes.Archive | FileAttributes.ReadOnly),
            fixture.Block.Rows[9].Attributes);
        var generationAfterClose = fixture.Block.Header.Generation;
        Assert.IsTrue(generationAfterClose > generationAfterExtend,
            "Close-only batch mutating metadata must advance Generation.");

        // Batch 4: Redundant close or batch with identical metadata produces no generation bump
        var fourth = fixture.Apply([Entry(9, 6, "fresh.txt",
            UsnReason.Close, closeMoment, sequenceNumber: 3, FileAttributes.Archive | FileAttributes.ReadOnly)]);
        Assert.AreEqual(0, fourth.Count);
        Assert.AreEqual(generationAfterClose, fixture.Block.Header.Generation,
            "Batch with no changes and no metadata mutation must not advance Generation.");
    }

    [TestMethod]
    public async Task CloseOnly_AfterAReportedChange_ProducesNoChangeAndNoMutation()
    {
        await using var fixture = new MutatorFixture();
        var first = fixture.Apply([Entry(7, 6, "notes.txt", UsnReason.DataExtend,
            fixture.Timestamp, sequenceNumber: 1)]);

        var second = fixture.Apply([Entry(7, 6, "notes.txt", UsnReason.Close,
            fixture.Timestamp.AddMinutes(1), sequenceNumber: 1)]);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(0, second.Count);
        Assert.AreEqual(fixture.Timestamp.Ticks, fixture.Block.Rows[7].ModifiedTicks);
    }

    [TestMethod]
    public async Task DeleteThenDeleteClose_AcrossBatches_ReportsOneDeleted()
    {
        await using var fixture = new MutatorFixture();

        var first = fixture.Apply([Entry(7, 6, "notes.txt", UsnReason.FileDelete,
            fixture.Timestamp, sequenceNumber: 1)]);
        var second = fixture.Apply([Entry(7, 6, "notes.txt", UsnReason.FileDelete | UsnReason.Close,
            fixture.Timestamp.AddMinutes(1), sequenceNumber: 1)]);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(FileChangeKind.Deleted, first[0].Kind);
        Assert.AreEqual(0, second.Count);
        Assert.IsTrue(fixture.Block.Rows[7].IsDeleted);
    }

    [TestMethod]
    public async Task RenameThenRenameClose_AcrossBatches_ReportsOneRenamed()
    {
        await using var fixture = new MutatorFixture();

        var first = fixture.Apply([Entry(7, 6, "renamed.txt", UsnReason.RenameNewName,
            fixture.Timestamp, sequenceNumber: 1)]);
        var second = fixture.Apply([Entry(7, 6, "renamed.txt", UsnReason.RenameNewName | UsnReason.Close,
            fixture.Timestamp.AddMinutes(1), sequenceNumber: 1)]);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(FileChangeKind.Renamed, first[0].Kind);
        Assert.AreEqual(0, second.Count);
    }

    [TestMethod]
    public async Task TwoRenamePairsWithoutAnInterveningClose_BothApplyAndTheCloseEchoesOnlyTheLast()
    {
        await using var fixture = new MutatorFixture();
        var closeMoment = fixture.Timestamp.AddMinutes(1);

        // NTFS emits one old-name/new-name record pair per rename and does not require a
        // close between renames: this open cycle renames notes.txt to first.txt, then
        // moves and renames it to second.txt at the root, all before the handle closes.
        var firstPair = fixture.Apply(
        [
            Entry(7, 6, "notes.txt", UsnReason.RenameOldName, fixture.Timestamp, sequenceNumber: 1),
            Entry(7, 6, "first.txt", UsnReason.RenameNewName, fixture.Timestamp, sequenceNumber: 1)
        ]);
        var secondPair = fixture.Apply(
        [
            Entry(7, 6, "first.txt", UsnReason.RenameOldName, fixture.Timestamp, sequenceNumber: 1),
            Entry(7, 5, "second.txt", UsnReason.RenameNewName, fixture.Timestamp, sequenceNumber: 1)
        ]);
        var close = fixture.Apply(
        [
            Entry(7, 5, "second.txt", UsnReason.RenameOldName | UsnReason.RenameNewName | UsnReason.Close,
                closeMoment, sequenceNumber: 1)
        ]);

        Assert.AreEqual(1, firstPair.Count);
        Assert.AreEqual(FileChangeKind.Renamed, firstPair[0].Kind);
        Assert.AreEqual(1, secondPair.Count,
            "A second rename inside one open cycle is a new rename, not the close record's echo.");
        Assert.AreEqual(FileChangeKind.Renamed, secondPair[0].Kind);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "documents", "first.txt"),
            secondPair[0].PreviousPath);
        Assert.AreEqual(0, close.Count,
            "The close record repeats the second rename's name and parent, so it is the echo.");
        Assert.AreEqual("second.txt", new string(NamePool.ReadRowName(fixture.Block, 7)));
        Assert.AreEqual(5u, fixture.Block.Rows[7].ParentRow);
        // The close record still restamps the row's metadata without a change event.
        Assert.AreEqual(closeMoment.Ticks, fixture.Block.Rows[7].ModifiedTicks);
    }

    [TestMethod]
    public async Task SameNameMoveInsideOneOpenCycle_ClassifiesAgainBecauseTheParentDiffers()
    {
        await using var fixture = new MutatorFixture();

        // A move that keeps the file name is still a rename pair in the journal; only the
        // parent changes. The echo key includes the parent, so this second pair classifies.
        fixture.Apply(
        [
            Entry(7, 6, "first.txt", UsnReason.RenameNewName, fixture.Timestamp, sequenceNumber: 1)
        ]);
        var move = fixture.Apply(
        [
            Entry(7, 5, "first.txt", UsnReason.RenameNewName, fixture.Timestamp, sequenceNumber: 1)
        ]);
        var close = fixture.Apply(
        [
            Entry(7, 5, "first.txt", UsnReason.RenameNewName | UsnReason.Close,
                fixture.Timestamp.AddMinutes(1), sequenceNumber: 1)
        ]);

        Assert.AreEqual(1, move.Count,
            "A RenameNewName carrying a parent the cycle has not applied is a move, not an echo.");
        Assert.AreEqual(FileChangeKind.Renamed, move[0].Kind);
        Assert.AreEqual(5u, fixture.Block.Rows[7].ParentRow);
        Assert.AreEqual("first.txt", new string(NamePool.ReadRowName(fixture.Block, 7)));
        Assert.AreEqual(0, close.Count);
    }

    [TestMethod]
    public async Task SequenceNumberChange_ResetsTheOpenCycleAndReportsTheNewIncarnation()
    {
        await using var fixture = new MutatorFixture();
        fixture.Apply([Entry(9, 6, "fresh.txt", UsnReason.FileCreate,
            fixture.Timestamp, sequenceNumber: 3)]);

        // The MFT segment was reused (sequence number moved on), so this close record
        // belongs to a new file, not to the reported cycle of the old one.
        var second = fixture.Apply([Entry(9, 6, "reused.txt", UsnReason.FileCreate | UsnReason.Close,
            fixture.Timestamp.AddMinutes(1), sequenceNumber: 4)]);

        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(FileChangeKind.Created, second[0].Kind);
    }
}
