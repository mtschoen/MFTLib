using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed partial class JournalMutatorTests : IAsyncDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    TempDirectory _directory = null!;
    BlockFile _block = null!;
    BlockWriter _writer = null!;
    DriveBlock _driveBlock = null!;
    DriveBlock[] _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-mutate");
        _block = TestBlocks.Create(Path.Combine(_directory.Path, "T-0BADF00D.mlix"), ProducerKind.Mft, 32);

        _writer = new BlockWriter(_block);
        var directoryColumns = new RowColumns(ParentRow: 0,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 0);
        _writer.TryWriteRow(0, "", directoryColumns);
        _writer.TryWriteRow(1, "Documents", directoryColumns);
        _writer.TryWriteRow(2, "existing.txt", new RowColumns(ParentRow: 1, RowFlags.InUse,
            Attributes: 32, Size: 100, Moment.Ticks, SequenceNumber: 0));
        _writer.Complete(Moment, null);

        _driveBlock = new DriveBlock('T', 0, _block, rootDirectoryPath: TestDriveRoot.For('T'));
        _snapshot = [_driveBlock];
    }

    public async ValueTask DisposeAsync()
    {
        await _snapshot.ReleaseNowAsync();
        _directory.Dispose();
    }

    [TestMethod]
    public void Create_FillsTheRowAtItsRecordNumber()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(5, 1, "brand-new.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.HasCount(1, changes);
        Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
        Assert.AreEqual("brand-new.txt", changes[0].Entry.Name);
        Assert.AreEqual(1u, _block.Rows[5].ParentRow);
        Assert.IsTrue(_block.Rows[5].IsInUse);
        Assert.IsFalse(_block.Rows[5].SizeKnown);
        Assert.IsFalse(changes[0].Entry.IsSizeKnown);
        Assert.IsFalse(changes[0].Entry.IsLastWriteTimeKnown);
        Assert.AreEqual(0L, _block.Rows[5].ModifiedTicks);
    }

    [TestMethod]
    public void Delete_SetsTheTombstoneAndKeepsTheName()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, "existing.txt", UsnReason.FileDelete | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.AreEqual(FileChangeKind.Deleted, changes[0].Kind);
        Assert.IsTrue(_block.Rows[2].IsDeleted);
        Assert.AreEqual("existing.txt", new string(NamePool.ReadRowName(_block, 2)));
        Assert.IsTrue(changes[0].Entry.IsDeleted);
    }

    [TestMethod]
    public void DeleteAfterCreateMissedAtCapacity_ProducesNoChangeForTheUnusedRow()
    {
        var mutator = new JournalMutator(_writer);
        var generationBefore = _block.Header.Generation;
        var nameTooLargeForTheRemainingPool = new string('n', 300);

        var changes = mutator.Apply(_snapshot[0],
        [
            CreateEntry(5, 1, nameTooLargeForTheRemainingPool,
                UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
            CreateEntry(5, 1, nameTooLargeForTheRemainingPool,
                UsnReason.FileDelete | UsnReason.Close, ChangeMoment)
        ], journalIdentifier: 7, nextUsn: 2000);

        Assert.IsEmpty(changes);
        Assert.IsTrue(mutator.CapacityExhausted);
        Assert.IsFalse(_block.Rows[5].IsInUse);
        Assert.IsFalse(_block.Rows[5].IsDeleted);
        Assert.AreEqual(generationBefore, _block.Header.Generation);
        Assert.AreEqual(7ul, _block.Header.UsnJournalIdentifier);
        Assert.AreEqual(2000L, _block.Header.UsnNextUsn);
    }

    [TestMethod]
    public void Rename_AppendsTheNewNameSwapsTheRowAndReportsThePreviousPath()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, "renamed.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.AreEqual(FileChangeKind.Renamed, changes[0].Kind);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "Documents", "existing.txt"), changes[0].PreviousPath);
        Assert.AreEqual("renamed.txt", new string(NamePool.ReadRowName(_block, 2)));
    }

    [TestMethod]
    public async Task Rename_CarriesBothTheOldAndTheNewFullPath()
    {
        await using var fixture = new MutatorFixture();
        var moved = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 7,
            ParentRecordNumber = 5,
            FileName = "renamed.txt",
            SequenceNumber = 1,
            Usn = 1000,
            Reason = UsnReason.RenameNewName,
            FileAttributes = FileAttributes.Archive,
            TimestampUtc = fixture.Timestamp
        });

        var change = fixture.Apply([moved]).Single();

        Assert.AreEqual(FileChangeKind.Renamed, change.Kind);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "renamed.txt"), change.Path);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "documents", "notes.txt"), change.PreviousPath);
    }

    [TestMethod]
    public async Task EachChange_KeepsThePathItHadWhenItWasApplied()
    {
        await using var fixture = new MutatorFixture();
        var created = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 9,
            ParentRecordNumber = 6,
            FileName = "first.txt",
            SequenceNumber = 1,
            Usn = 1000,
            Reason = UsnReason.FileCreate,
            FileAttributes = FileAttributes.Archive,
            TimestampUtc = fixture.Timestamp
        });
        var renamed = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 9,
            ParentRecordNumber = 5,
            FileName = "second.txt",
            SequenceNumber = 1,
            Usn = 1001,
            Reason = UsnReason.RenameNewName,
            FileAttributes = FileAttributes.Archive,
            TimestampUtc = fixture.Timestamp
        });

        var changes = fixture.Apply([created, renamed]);

        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "documents", "first.txt"), changes[0].Path);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "second.txt"), changes[1].Path);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "documents", "first.txt"), changes[1].PreviousPath);
    }

    [TestMethod]
    public async Task Delete_CarriesThePathOfTheRowItTombstoned()
    {
        await using var fixture = new MutatorFixture();
        var deleted = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 7,
            ParentRecordNumber = 6,
            FileName = "notes.txt",
            SequenceNumber = 1,
            Usn = 1000,
            Reason = UsnReason.FileDelete,
            FileAttributes = FileAttributes.Archive,
            TimestampUtc = fixture.Timestamp
        });

        var change = fixture.Apply([deleted]).Single();

        Assert.AreEqual(FileChangeKind.Deleted, change.Kind);
        Assert.AreEqual(Path.Combine(TestDriveRoot.For('T'), "documents", "notes.txt"), change.Path);
        Assert.IsNull(change.PreviousPath);
    }

    [TestMethod]
    public void MoveOnlyFrame_DoesNotGrowTheNamePoolWhileARealRenameDoes()
    {
        var mutator = new JournalMutator(_writer);
        var namePoolUsedBefore = _block.Header.NamePoolUsed;

        mutator.Apply(_snapshot[0],
            [CreateEntry(2, 0, "existing.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.AreEqual(0u, _block.Rows[2].ParentRow);
        Assert.AreEqual(namePoolUsedBefore, _block.Header.NamePoolUsed);
        var namePoolUsedAfterMove = _block.Header.NamePoolUsed;

        mutator.Apply(_snapshot[0],
            [CreateEntry(2, 0, "renamed.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2100);

        Assert.IsGreaterThan(namePoolUsedAfterMove, _block.Header.NamePoolUsed);
    }

    [TestMethod]
    public void RenameOldName_ProducesNoChangeBecauseTheNewNameFrameCarriesTheRename()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, "existing.txt", UsnReason.RenameOldName, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.IsEmpty(changes);
        Assert.AreEqual("existing.txt", new string(NamePool.ReadRowName(_block, 2)));
    }

    [TestMethod]
    public void DataChange_InvalidatesTheSizeAndLastWriteTimeAndReportsModified()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, "existing.txt", UsnReason.DataExtend | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.AreEqual(FileChangeKind.Modified, changes[0].Kind);
        Assert.IsFalse(_block.Rows[2].LastWriteTimeKnown);
        Assert.AreEqual(0L, _block.Rows[2].ModifiedTicks);
        Assert.IsFalse(_block.Rows[2].SizeKnown);
        Assert.AreEqual(0L, _block.Rows[2].Size);
    }

    [TestMethod]
    public void CloseOnly_ProducesNoChange()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, "existing.txt", UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void RecordNumberPastSlotCapacity_SetsCapacityExhaustedAndKeepsApplyingTheRest()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
        [
            CreateEntry(9999, 1, "far.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
            CreateEntry(6, 1, "near.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)
        ], journalIdentifier: 7, nextUsn: 2000);

        Assert.IsTrue(mutator.CapacityExhausted);
        Assert.IsTrue(_block.Header.IsCapacityExhausted);
        Assert.HasCount(1, changes);
        Assert.AreEqual("near.txt", changes[0].Entry.Name);
    }

    [TestMethod]
    public void ExhaustedNamePool_SetsCapacityExhaustedAndDoesNotThrow()
    {
        var mutator = new JournalMutator(_writer);
        var longName = new string('n', 300);
        var entries = new List<UsnJournalEntry>();
        for (var recordNumber = 5ul; recordNumber < 12; recordNumber++)
        {
            entries.Add(CreateEntry(recordNumber, 1, longName, UsnReason.FileCreate | UsnReason.Close, ChangeMoment));
        }

        mutator.Apply(_snapshot[0], entries, journalIdentifier: 7, nextUsn: 2000);

        Assert.IsTrue(mutator.CapacityExhausted);
    }

    [TestMethod]
    public void Create_OfADirectory_SetsTheDirectoryFlagAndLeavesSizeUnknown()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(5, 1, "NewFolder", UsnReason.FileCreate | UsnReason.Close, ChangeMoment,
                fileAttributes: FileAttributes.Directory)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.HasCount(1, changes);
        Assert.IsTrue(_block.Rows[5].IsDirectory);
        Assert.IsFalse(_block.Rows[5].SizeKnown);
    }

    [TestMethod]
    public void Rename_NamePoolExhausted_ReturnsNoChangeAndMarksCapacityExhausted()
    {
        var mutator = new JournalMutator(_writer);
        // Longer than the whole 512-byte name pool, so the append this rename needs can
        // never fit regardless of what else has or has not been written yet.
        var hugeName = new string('a', 300);

        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, hugeName, UsnReason.RenameNewName | UsnReason.RenameOldName | UsnReason.Close,
                ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);

        Assert.IsEmpty(changes);
        Assert.IsTrue(_block.Header.IsCapacityExhausted);
        Assert.AreEqual("existing.txt", new string(NamePool.ReadRowName(_block, 2)));
    }

    [TestMethod]
    public void Apply_WritesTheCursorAndBumpsTheGenerationExactlyOncePerBatch()
    {
        var mutator = new JournalMutator(_writer);
        var generationBefore = _block.Header.Generation;

        mutator.Apply(_snapshot[0],
        [
            CreateEntry(5, 1, "one.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
            CreateEntry(6, 1, "two.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)
        ], journalIdentifier: 0xABCD, nextUsn: 9999);

        Assert.AreEqual(generationBefore + 1, _block.Header.Generation);
        Assert.AreEqual(0xABCDul, _block.Header.UsnJournalIdentifier);
        Assert.AreEqual(9999L, _block.Header.UsnNextUsn);
    }

    [TestMethod]
    public void EmptyBatch_StillAdvancesTheCursorWithoutBumpingTheGeneration()
    {
        var mutator = new JournalMutator(_writer);
        var generationBefore = _block.Header.Generation;

        var changes = mutator.Apply(_snapshot[0], [], journalIdentifier: 0xABCD, nextUsn: 4242);

        Assert.IsEmpty(changes);
        Assert.AreEqual(generationBefore, _block.Header.Generation);
        Assert.AreEqual(4242L, _block.Header.UsnNextUsn);
    }

    [TestMethod]
    [DataRow(1ul, "reused.txt")]
    [DataRow(0ul, "reincarnated.txt")]
    public void CreateOverATombstonedSlot_ProducesALiveRowWithTheNewNameParentAndColumns(
        ulong parentRecordNumber, string fileName)
    {
        var mutator = new JournalMutator(_writer);
        mutator.Apply(_snapshot[0],
            [CreateEntry(2, 1, "existing.txt", UsnReason.FileDelete | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2000);
        Assert.IsTrue(_block.Rows[2].IsDeleted);

        var changes = mutator.Apply(_snapshot[0],
            [CreateEntry(2, parentRecordNumber, fileName, UsnReason.FileCreate | UsnReason.Close, ChangeMoment)],
            journalIdentifier: 7, nextUsn: 2100);

        Assert.HasCount(1, changes);
        Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
        Assert.IsTrue(_block.Rows[2].IsInUse);
        Assert.IsFalse(_block.Rows[2].IsDeleted);
        Assert.AreEqual((uint)parentRecordNumber, _block.Rows[2].ParentRow);
        Assert.AreEqual((uint)FileAttributes.Archive, _block.Rows[2].Attributes);
        Assert.AreEqual(0L, _block.Rows[2].Size);
        Assert.IsFalse(_block.Rows[2].SizeKnown);
        Assert.IsFalse(_block.Rows[2].LastWriteTimeKnown);
        Assert.AreEqual(0L, _block.Rows[2].ModifiedTicks);
        Assert.AreEqual(fileName, new string(NamePool.ReadRowName(_block, 2)));
    }
}
