using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class CapacityExhaustionTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-exhaust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A just-unmapped block file can stay locked briefly on Windows.
        }
    }

    (BlockFile Block, BlockWriter Writer, Snapshot Snapshot) BuildTinyDrive(
        uint slotCapacity, uint namePoolCapacity)
    {
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(_directory, "T-00000001.mlix"),
            VolumeSerial = 1,
            ProducerKind = ProducerKind.Mft,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity
        });

        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", new RowColumns(ParentRow: 0,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks));
        writer.Complete(Moment);
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, deleteFileOnRelease: false)]);
        return (block, writer, snapshot);
    }

    static UsnJournalEntry Create(ulong recordNumber, string fileName)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = 0,
            Usn = 1,
            Timestamp = Moment,
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public void SlotExhaustion_FlagsCompactionKeepsWhatFitsAndNeverThrows()
    {
        var (block, writer, snapshot) = BuildTinyDrive(slotCapacity: 4, namePoolCapacity: 512);
        try
        {
            var mutator = new JournalMutator(writer);
            var changes = mutator.Apply(snapshot, 0,
                [Create(1, "fits.txt"), Create(400, "does-not-fit.txt"), Create(2, "also-fits.txt")],
                journalId: 1, nextUsn: 10);

            Assert.AreEqual(2, changes.Count);
            Assert.IsTrue(block.Header.IsCompactionNeeded);
            Assert.AreEqual("fits.txt", new string(NamePool.ReadRowName(block, 1)));
            Assert.AreEqual("also-fits.txt", new string(NamePool.ReadRowName(block, 2)));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void NamePoolExhaustion_FlagsCompactionAndNeverThrows()
    {
        // Capacity for exactly one "name-number-N.txt" name (17 characters, 34 bytes) with no
        // room left for a second one, so the first create survives and every later one is the
        // exhaustion case, mirroring the fits/does-not-fit split in the slot exhaustion test.
        var (block, writer, snapshot) = BuildTinyDrive(slotCapacity: 64, namePoolCapacity: 40);
        try
        {
            var mutator = new JournalMutator(writer);
            var entries = new List<UsnJournalEntry>();
            for (var recordNumber = 1ul; recordNumber <= 10; recordNumber++)
            {
                entries.Add(Create(recordNumber, $"name-number-{recordNumber}.txt"));
            }

            var changes = mutator.Apply(snapshot, 0, entries, journalId: 1, nextUsn: 20);

            Assert.AreEqual(1, changes.Count);
            Assert.IsTrue(block.Header.IsCompactionNeeded);
            Assert.IsTrue(block.Header.NamePoolUsed <= block.Header.NamePoolCapacity);
            Assert.AreEqual("name-number-1.txt", new string(NamePool.ReadRowName(block, 1)));
            Assert.IsFalse(block.Rows[2].IsInUse);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void SearchOverAnExhaustedBlock_StillReturnsWhatWasApplied()
    {
        var (_, writer, snapshot) = BuildTinyDrive(slotCapacity: 4, namePoolCapacity: 512);
        try
        {
            var mutator = new JournalMutator(writer);
            mutator.Apply(snapshot, 0, [Create(1, "survivor.txt"), Create(900, "lost.txt")],
                journalId: 1, nextUsn: 30);

            var results = SearchEngineTestAccess.Search(snapshot, new SearchQuery("survivor"));
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(0, SearchEngineTestAccess.Search(snapshot, new SearchQuery("lost")).Count);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
}
