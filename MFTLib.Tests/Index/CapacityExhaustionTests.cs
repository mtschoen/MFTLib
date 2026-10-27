using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class CapacityExhaustionTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    TempDirectory _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = new TempDirectory("mftlib-exhaust");

    public void Dispose() => _directory.Dispose();

    (BlockFile Block, BlockWriter Writer, RootBlock[] Snapshot) BuildTinyRoot(
        uint slotCapacity, uint namePoolCapacity)
    {
        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to the returned DriveBlock snapshot, which callers release via ReleaseNowAsync
        var block = TestBlocks.Create(Path.Combine(_directory.Path, "T-00000001.mlix"), ProducerKind.Mft, slotCapacity, namePoolCapacity, volumeSerial: 1);

        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", new RowColumns(ParentRow: 0,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 0));
        writer.Complete(Moment, null);
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'))];
        return (block, writer, snapshot);
    }

    static UsnJournalEntry Create(ulong recordNumber, string fileName)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = 0,
            Usn = 1,
            TimestampUtc = Moment,
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task SlotExhaustion_FlagsCapacityExhaustionKeepsWhatFitsAndNeverThrows()
    {
        var (block, writer, snapshot) = BuildTinyRoot(slotCapacity: 4, namePoolCapacity: 512);
        try
        {
            var mutator = new JournalMutator(writer);
            var changes = mutator.Apply(snapshot[0],
                [Create(1, "fits.txt"), Create(400, "does-not-fit.txt"), Create(2, "also-fits.txt")],
                journalIdentifier: 1, nextUsn: 10);

            Assert.HasCount(2, changes);
            Assert.IsTrue(block.Header.IsCapacityExhausted);
            Assert.AreEqual("fits.txt", new string(NamePool.ReadRowName(block, 1)));
            Assert.AreEqual("also-fits.txt", new string(NamePool.ReadRowName(block, 2)));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task NamePoolExhaustion_FlagsCapacityExhaustionAndNeverThrows()
    {
        // Capacity for exactly one "name-number-N.txt" name (17 characters, 34 bytes) with no
        // room left for a second one, so the first create survives and every later one is the
        // exhaustion case, mirroring the fits/does-not-fit split in the slot exhaustion test.
        var (block, writer, snapshot) = BuildTinyRoot(slotCapacity: 64, namePoolCapacity: 40);
        try
        {
            var mutator = new JournalMutator(writer);
            var entries = new List<UsnJournalEntry>();
            for (var recordNumber = 1ul; recordNumber <= 10; recordNumber++)
            {
                entries.Add(Create(recordNumber, $"name-number-{recordNumber}.txt"));
            }

            var changes = mutator.Apply(snapshot[0], entries, journalIdentifier: 1, nextUsn: 20);

            Assert.HasCount(1, changes);
            Assert.IsTrue(block.Header.IsCapacityExhausted);
            Assert.IsLessThanOrEqualTo(block.Header.NamePoolCapacity, block.Header.NamePoolUsed);
            Assert.AreEqual("name-number-1.txt", new string(NamePool.ReadRowName(block, 1)));
            Assert.IsFalse(block.Rows[2].IsInUse);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task SearchOverAnExhaustedBlock_StillReturnsWhatWasApplied()
    {
        var (_, writer, snapshot) = BuildTinyRoot(slotCapacity: 4, namePoolCapacity: 512);
        try
        {
            var mutator = new JournalMutator(writer);
            mutator.Apply(snapshot[0], [Create(1, "survivor.txt"), Create(900, "lost.txt")],
                journalIdentifier: 1, nextUsn: 30);

            var results = SearchEngine.Search(snapshot[0], new SearchQuery("survivor"), TestContext.CancellationToken);
            Assert.HasCount(1, results);
            Assert.IsEmpty(SearchEngine.Search(snapshot[0], new SearchQuery("lost"), TestContext.CancellationToken));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
