using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftProducerEndToEndTests
{
    static readonly DateTime Modified = new(2026, 9, 3, 12, 30, 0, DateTimeKind.Utc);
    static readonly UsnJournalCursor AdvancedCursor = new(71, 12500);
    string _rootDirectory = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"mft-end-to-end-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(_rootDirectory, "cache");
        Directory.CreateDirectory(_rootDirectory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_rootDirectory, recursive: true);

    [TestMethod]
    public async Task OpenAsync_AdoptsBrokerBlockAndAppliesCatchUpAtRecordNumbers()
    {
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, _, _) => Records(),
            catchUp: (_, _) => (CatchUpEntries(), AdvancedCursor));
        BrokerScanResult? completed = null;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync,
            scanCompleted: result => completed = result).CreateProducer();
        await using var index = await FileIndex.OpenAsync(Options(producer), harness.CancellationToken);

        AssertReady(index);
        Assert.AreEqual(24u, index.Drives[0].RowCount);
        Assert.AreEqual(@"C:\", index.Root('C').Path);
        var notes = index.Find(@"C:\documents\notes.txt")!.Value;
        Assert.AreEqual(20UL, notes.Id.RecordNumber);
        Assert.AreEqual(4096L, notes.Size);
        Assert.IsTrue(notes.SizeKnown);
        Assert.AreEqual(Modified, notes.Modified);
        CollectionAssert.AreEquivalent(new ulong[] { 20, 22 }, index.Search(new SearchQuery("notes.txt"))
            .Select(entry => entry.Id.RecordNumber).ToArray());
        var duplicates = index.DuplicateNames().Single();
        Assert.AreEqual("notes.txt", duplicates.Name);
        Assert.AreEqual(2, duplicates.Entries.Count);
        var deleted = index.Find(@"C:\documents\obsolete.txt")!.Value;
        var renamed = index.Find(@"C:\documents\draft.txt")!.Value;
        Assert.IsNotNull(completed);
        Assert.AreEqual(0, completed.Errors.Count);
        Assert.AreEqual(InProcessBlockBrokerHarness.ArmedCursor, completed.ArmedCursors["C"]);
        var block = completed.BlockOutcomes["C"].Block;
        Assert.AreSame(block, index.Root('C').DriveBlock.Block);
        Assert.AreEqual(completed.ArmedCursors["C"].NextUsn, block.Header.UsnNextUsn);
        Assert.IsNull(index.Find(@"C:\documents\created.txt"));

        var cursor = completed.AdvancedCursors["C"];
        var changes = index.ApplyJournalEntries('C', completed.CatchUpEntries["C"], cursor.JournalId, cursor.NextUsn);

        CollectionAssert.AreEqual(new[] { FileChangeKind.Created, FileChangeKind.Deleted, FileChangeKind.Renamed },
            changes.Select(change => change.Kind).ToArray());
        Assert.AreEqual(30UL, index.Find(@"C:\documents\created.txt")!.Value.Id.RecordNumber);
        Assert.IsTrue(deleted.IsDeleted);
        Assert.IsTrue(block.Rows[21].IsDeleted);
        Assert.IsNull(index.Find(@"C:\documents\obsolete.txt"));
        Assert.AreEqual("published.txt", renamed.Name);
        Assert.AreEqual(23UL, index.Find(@"C:\documents\published.txt")!.Value.Id.RecordNumber);
        Assert.IsNull(index.Find(@"C:\documents\draft.txt"));
        Assert.AreEqual(AdvancedCursor.JournalId, block.Header.UsnJournalId);
        Assert.AreEqual(AdvancedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.IsTrue(block.Header.UsnNextUsn > completed.ArmedCursors["C"].NextUsn);
    }

    [TestMethod]
    public async Task OpenAsync_BlockProgressAndDirectoryProfileSurviveTheProtocol()
    {
        var parsingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<BrokerScanProgress>();
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, reporter, cancellationToken) =>
        {
            reporter?.Report(new BlockWriteProgress(6, 0, 6, null, BrokerScanPhase.Parsing));
            parsingReceived.Task.Wait(cancellationToken);
            return Records();
        });
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync, new BrokerScanOptions
        {
            Profile = BrokerScanProfile.DirectoryIndex,
            KeepFileNames = ["NOTES.TXT"],
            Progress = new CallbackProgress(value =>
            {
                progress.Add(value);
                if (value.Phase == BrokerScanPhase.Parsing)
                {
                    parsingReceived.TrySetResult();
                }
            })
        }).CreateProducer();
        await using var index = await FileIndex.OpenAsync(Options(producer), harness.CancellationToken);

        AssertReady(index);
        Assert.AreEqual(BrokerScanPhase.Parsing, progress[0].Phase);
        Assert.AreEqual(BrokerScanPhase.Transferring, progress[^1].Phase);
        Assert.IsTrue(progress.All(value => value.DriveLetter == "C"));
        Assert.IsTrue(progress[^1].BytesProcessed > 0);
        Assert.IsTrue(index.Find(@"C:\documents")!.Value.IsDirectory);
        Assert.IsNotNull(index.Find(@"C:\documents\notes.txt"));
        Assert.IsNotNull(index.Find(@"C:\notes.txt"));
        Assert.IsNull(index.Find(@"C:\documents\obsolete.txt"));
        Assert.IsNull(index.Find(@"C:\documents\draft.txt"));
    }

    [TestMethod]
    public async Task OpenAsync_RecordBeyondPlannedCapacityMarksDriveStale()
    {
        await using var harness = new InProcessBlockBrokerHarness(
            recordBatches: (_, _, _) => Records().Append([Record(1_000_000, 5, "beyond.txt")]),
            volumeInformation: new NtfsVolumeInformation(1024, 1024, 0, 0, 0, 0));
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync).CreateProducer();
        await using var index = await FileIndex.OpenAsync(Options(producer), harness.CancellationToken);

        Assert.IsTrue(harness.CreatedBlock!.Header.SlotCapacity < 1_000_000);
        Assert.IsTrue(harness.CreatedBlock.Header.IsComplete);
        Assert.IsTrue(harness.CreatedBlock.Header.IsCompactionNeeded);
        Assert.IsTrue(index.Drives[0].CompactionNeeded);
        Assert.AreEqual(DriveState.Stale, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsNotNull(index.Find(@"C:\documents\notes.txt"));
        Assert.IsNull(index.Find(@"C:\beyond.txt"));
    }

    [TestMethod]
    public async Task BlockSession_ReplacesParkedWatchCursorAndIndexRescanPreservesOldHandles()
    {
        await AssertParkedWatchCursorAsync();
        var scanCount = 0;
        var scans = new List<BrokerScanResult>();
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, _, _) =>
            ++scanCount == 1 ? Records() : [[Record(5, 5, ".", directory: true), Record(40, 5, "replacement.txt")]]);
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync, scanCompleted: scans.Add).CreateProducer();
        await using var index = await FileIndex.OpenAsync(Options(producer), harness.CancellationToken);
        var previous = index.Find(@"C:\documents\notes.txt")!.Value;
        var previousBlock = previous.DriveBlock.Block;

        await index.RescanAsync('C', harness.CancellationToken);

        Assert.AreEqual(2, scanCount);
        Assert.AreEqual(2, scans.Count);
        AssertReady(index);
        Assert.AreNotSame(previousBlock, index.Root('C').DriveBlock.Block);
        Assert.AreSame(scans[1].BlockOutcomes["C"].Block, index.Root('C').DriveBlock.Block);
        Assert.AreEqual(40UL, index.Find(@"C:\replacement.txt")!.Value.Id.RecordNumber);
        Assert.IsNull(index.Find(@"C:\documents\notes.txt"));
        Assert.AreEqual(@"C:\documents\notes.txt", previous.Path);
        Assert.AreEqual("notes.txt", previous.Name);
        Assert.AreEqual(4096L, previous.Size);
        Assert.AreEqual(Modified, previous.Modified);
        Assert.IsFalse(previous.IsDeleted);
    }

    [TestMethod]
    public async Task OpenAsync_CatchUpFailureWarnsAndAdoptsCompleteBlockWithFreshCursor()
    {
        var cursorQueries = 0;
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, _, _) => Records(),
            catchUp: (_, _) => throw new IOException("journal wrapped during scan"),
            queryCursor: _ => ++cursorQueries == 1 ? InProcessBlockBrokerHarness.ArmedCursor : AdvancedCursor);
        BrokerScanResult? completed = null;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync,
            scanCompleted: result => completed = result).CreateProducer();
        await using var index = await FileIndex.OpenAsync(Options(producer), harness.CancellationToken);

        Assert.IsNotNull(completed);
        StringAssert.Contains(completed.Warnings["C"], "journal wrapped during scan");
        Assert.AreEqual(0, completed.Errors.Count);
        Assert.AreEqual(2, cursorQueries);
        Assert.AreEqual(AdvancedCursor, completed.AdvancedCursors["C"]);
        Assert.AreEqual(0, completed.CatchUpEntries["C"].Length);
        var block = completed.BlockOutcomes["C"].Block;
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(InProcessBlockBrokerHarness.ArmedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.AreSame(block, index.Root('C').DriveBlock.Block);
        AssertReady(index);
        Assert.IsNotNull(index.Find(@"C:\documents\notes.txt"));
    }

    static async Task AssertParkedWatchCursorAsync()
    {
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, _, _) => Records());
        await using var session = await JournalBrokerScanSession.StartAsync(harness.ConnectAsync, ["C"],
            new BrokerScanOptions
            {
                BlockTargets = new Dictionary<string, BlockScanTarget>
                {
                    ["C"] = new(harness.Request.BlockPath, 123, true)
                }
            }, harness.CancellationToken);
        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.IsNotNull(session.LatestScan);
        Assert.AreEqual(0, session.LatestScan.Errors.Count);
        var block = session.LatestScan.BlockOutcomes["C"].Block;
        Assert.IsTrue(block.Header.IsComplete);
        var cursor = new UsnJournalCursor(block.Header.UsnJournalId, block.Header.UsnNextUsn);
        Assert.AreNotEqual(cursor, session.WatchCursors["C"]);

        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { [@"c:\"] = cursor });

        Assert.AreEqual(1, session.WatchCursors.Count);
        Assert.AreEqual(cursor, session.WatchCursors["C"]);
        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
    }

    FileIndexOptions Options(MftBlockProducer producer) => new()
    {
        Drives = [new IndexedDrive('C', _rootDirectory, 123)],
        CacheDirectory = _cacheDirectory,
        ProducerPolicy = ProducerPolicy.MftOnly,
        MftProducer = producer
    };

    static void AssertReady(FileIndex index)
    {
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.IsFalse(index.Drives[0].CompactionNeeded);
        Assert.IsNull(index.Drives[0].MftProducerFailureMessage);
    }

    static IEnumerable<IReadOnlyList<MftRecord>> Records() =>
    [
        [Record(5, 5, ".", directory: true), Record(10, 5, "documents", directory: true)],
        [Record(20, 10, "notes.txt"), Record(21, 10, "obsolete.txt"),
            Record(22, 5, "notes.txt"), Record(23, 10, "draft.txt")]
    ];

    static MftRecord Record(ulong recordNumber, ulong parentRecordNumber, string name, bool directory = false) =>
        new(recordNumber, parentRecordNumber,
            new MftRecordFields(directory ? (ushort)3 : (ushort)1,
                directory ? FileAttributes.Directory : FileAttributes.Normal,
                directory ? 0 : 4096, Modified.ToFileTimeUtc()), name, null);

    static UsnJournalEntry[] CatchUpEntries() =>
    [
        JournalEntry(30, "created.txt", UsnReason.FileCreate, 12400),
        JournalEntry(21, "obsolete.txt", UsnReason.FileDelete, 12410),
        JournalEntry(23, "published.txt", UsnReason.RenameNewName, 12420)
    ];

    static UsnJournalEntry JournalEntry(ulong recordNumber, string name, UsnReason reason, long journalPosition) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = 10,
            FileName = name,
            Reason = reason,
            Usn = journalPosition,
            Timestamp = Modified,
            FileAttributes = FileAttributes.Normal
        });

    sealed class CallbackProgress(Action<BrokerScanProgress> report) : IProgress<BrokerScanProgress>
    {
        public void Report(BrokerScanProgress value) => report(value);
    }
}
