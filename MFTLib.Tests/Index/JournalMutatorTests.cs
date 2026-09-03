using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class JournalMutatorTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    string _directory = null!;
    BlockFile _block = null!;
    BlockWriter _writer = null!;
    DriveBlock _driveBlock = null!;
    Snapshot _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-mutate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(_directory, "T-0BADF00D.mlix"),
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Mft,
            SlotCapacity = 32,
            NamePoolCapacity = 512
        });

        _writer = new BlockWriter(_block);
        var directoryColumns = new RowColumns(ParentRow: 0,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks);
        _writer.TryWriteRow(0, "", directoryColumns);
        _writer.TryWriteRow(1, "Documents", directoryColumns);
        _writer.TryWriteRow(2, "existing.txt", new RowColumns(ParentRow: 1, RowFlags.InUse,
            Attributes: 32, Size: 100, Moment.Ticks));
        _writer.Complete(Moment);

        _driveBlock = new DriveBlock('T', 0, _block, deleteFileOnRelease: false);
        _snapshot = Snapshot.Create([_driveBlock]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A just-unmapped file can stay locked briefly on Windows.
        }
    }

    static UsnJournalEntry Entry(ulong recordNumber, ulong parentRecordNumber, string fileName,
        UsnReason reason, DateTime timestamp)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            Usn = 1000,
            Timestamp = timestamp,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public void Create_FillsTheRowAtItsRecordNumber()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
            [Entry(5, 1, "brand-new.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
        Assert.AreEqual("brand-new.txt", changes[0].Entry.Name);
        Assert.AreEqual(1u, _block.Rows[5].ParentRow);
        Assert.IsTrue(_block.Rows[5].IsInUse);
        Assert.IsFalse(_block.Rows[5].SizeKnown);
        Assert.IsFalse(changes[0].Entry.SizeKnown);
        Assert.AreEqual(ChangeMoment.Ticks, _block.Rows[5].ModifiedTicks);
    }

    [TestMethod]
    public void Delete_SetsTheTombstoneAndKeepsTheName()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "existing.txt", UsnReason.FileDelete | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(FileChangeKind.Deleted, changes[0].Kind);
        Assert.IsTrue(_block.Rows[2].IsDeleted);
        Assert.AreEqual("existing.txt", new string(NamePool.ReadRowName(_block, 2)));
        Assert.IsTrue(changes[0].Entry.IsDeleted);
    }

    [TestMethod]
    public void Rename_AppendsTheNewNameSwapsTheRowAndReportsThePreviousName()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "renamed.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(FileChangeKind.Renamed, changes[0].Kind);
        Assert.AreEqual("existing.txt", changes[0].PreviousName);
        Assert.AreEqual("renamed.txt", new string(NamePool.ReadRowName(_block, 2)));
    }

    [TestMethod]
    public void Move_WritesTheNewParentRow()
    {
        var mutator = new JournalMutator(_writer);
        mutator.Apply(_snapshot, 0,
            [Entry(2, 0, "existing.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(0u, _block.Rows[2].ParentRow);
    }

    [TestMethod]
    public void MoveOnlyFrame_DoesNotGrowTheNamePoolWhileARealRenameDoes()
    {
        var mutator = new JournalMutator(_writer);
        var namePoolUsedBefore = _block.Header.NamePoolUsed;

        mutator.Apply(_snapshot, 0,
            [Entry(2, 0, "existing.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(namePoolUsedBefore, _block.Header.NamePoolUsed);
        var namePoolUsedAfterMove = _block.Header.NamePoolUsed;

        mutator.Apply(_snapshot, 0,
            [Entry(2, 0, "renamed.txt", UsnReason.RenameNewName | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2100);

        Assert.IsTrue(_block.Header.NamePoolUsed > namePoolUsedAfterMove);
    }

    [TestMethod]
    public void RenameOldName_ProducesNoChangeBecauseTheNewNameFrameCarriesTheRename()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "existing.txt", UsnReason.RenameOldName, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(0, changes.Count);
        Assert.AreEqual("existing.txt", new string(NamePool.ReadRowName(_block, 2)));
    }

    [TestMethod]
    public void DataChange_UpdatesTheModifiedColumnAndReportsModified()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "existing.txt", UsnReason.DataExtend | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(FileChangeKind.Modified, changes[0].Kind);
        Assert.AreEqual(ChangeMoment.Ticks, _block.Rows[2].ModifiedTicks);
        Assert.AreEqual(100L, _block.Rows[2].Size);
    }

    [TestMethod]
    public void CloseOnly_ProducesNoChange()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "existing.txt", UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        Assert.AreEqual(0, changes.Count);
    }

    [TestMethod]
    public void RecordNumberPastSlotCapacity_SetsCompactionNeededAndKeepsApplyingTheRest()
    {
        var mutator = new JournalMutator(_writer);
        var changes = mutator.Apply(_snapshot, 0,
        [
            Entry(9999, 1, "far.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
            Entry(6, 1, "near.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)
        ], journalId: 7, nextUsn: 2000);

        Assert.IsTrue(mutator.CompactionNeeded);
        Assert.IsTrue(_block.Header.IsCompactionNeeded);
        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual("near.txt", changes[0].Entry.Name);
    }

    [TestMethod]
    public void ExhaustedNamePool_SetsCompactionNeededAndDoesNotThrow()
    {
        var mutator = new JournalMutator(_writer);
        var longName = new string('n', 300);
        var entries = new List<UsnJournalEntry>();
        for (var recordNumber = 5ul; recordNumber < 12; recordNumber++)
        {
            entries.Add(Entry(recordNumber, 1, longName, UsnReason.FileCreate | UsnReason.Close, ChangeMoment));
        }

        mutator.Apply(_snapshot, 0, entries, journalId: 7, nextUsn: 2000);

        Assert.IsTrue(mutator.CompactionNeeded);
    }

    [TestMethod]
    public void Apply_WritesTheCursorAndBumpsTheGenerationExactlyOncePerBatch()
    {
        var mutator = new JournalMutator(_writer);
        var generationBefore = _block.Header.Generation;

        mutator.Apply(_snapshot, 0,
        [
            Entry(5, 1, "one.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
            Entry(6, 1, "two.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)
        ], journalId: 0xABCD, nextUsn: 9999);

        Assert.AreEqual(generationBefore + 1, _block.Header.Generation);
        Assert.AreEqual(0xABCDul, _block.Header.UsnJournalId);
        Assert.AreEqual(9999L, _block.Header.UsnNextUsn);
    }

    [TestMethod]
    public void EmptyBatch_StillAdvancesTheCursorWithoutBumpingTheGeneration()
    {
        var mutator = new JournalMutator(_writer);
        var generationBefore = _block.Header.Generation;

        var changes = mutator.Apply(_snapshot, 0, [], journalId: 0xABCD, nextUsn: 4242);

        Assert.AreEqual(0, changes.Count);
        Assert.AreEqual(generationBefore, _block.Header.Generation);
        Assert.AreEqual(4242L, _block.Header.UsnNextUsn);
    }

    [TestMethod]
    public void CreateOverATombstonedSlot_ClearsTheTombstone()
    {
        var mutator = new JournalMutator(_writer);
        mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "existing.txt", UsnReason.FileDelete | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);
        Assert.IsTrue(_block.Rows[2].IsDeleted);

        mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "reused.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2100);

        Assert.IsFalse(_block.Rows[2].IsDeleted);
        Assert.AreEqual("reused.txt", new string(NamePool.ReadRowName(_block, 2)));
    }

    [TestMethod]
    public void CreateReusingATombstonedRecordNumber_ProducesALiveRowWithTheNewNameParentAndColumns()
    {
        var mutator = new JournalMutator(_writer);
        mutator.Apply(_snapshot, 0,
            [Entry(2, 1, "existing.txt", UsnReason.FileDelete | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2000);

        var changes = mutator.Apply(_snapshot, 0,
            [Entry(2, 0, "reincarnated.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)],
            journalId: 7, nextUsn: 2100);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
        Assert.IsTrue(_block.Rows[2].IsInUse);
        Assert.IsFalse(_block.Rows[2].IsDeleted);
        Assert.AreEqual(0u, _block.Rows[2].ParentRow);
        Assert.AreEqual((uint)FileAttributes.Archive, _block.Rows[2].Attributes);
        Assert.AreEqual(0L, _block.Rows[2].Size);
        Assert.IsFalse(_block.Rows[2].SizeKnown);
        Assert.AreEqual(ChangeMoment.Ticks, _block.Rows[2].ModifiedTicks);
        Assert.AreEqual("reincarnated.txt", new string(NamePool.ReadRowName(_block, 2)));
    }
}
