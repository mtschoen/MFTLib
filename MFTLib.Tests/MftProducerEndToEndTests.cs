using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The catch-up loss test installs JournalCheckpointCheck's process-wide journal override.
[TestClass]
[DoNotParallelize]
public class MftProducerEndToEndTests : BrokerBlockTestBase
{
    static readonly DateTime Modified = new(2026, 9, 3, 12, 30, 0, DateTimeKind.Utc);
    static readonly UsnJournalCursor RearmedCursor = new(71, 13000);
    static readonly JournalWindow TrimmedWindow = new(71, 20000, 30000, 4096, 32768);
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
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _, _) => Records(),
            readJournal: CatchUpSources.ToTip(AdvancedCursor, CatchUpEntries())));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard);

        AssertReady(index);
        Assert.AreEqual(24u, index.HeaderOf('C').RowCount);
        Assert.AreEqual(_rootDirectory, index.Root('C').Path);
        var notes = index.Find(At("documents", "notes.txt"))!.Value;
        Assert.AreEqual(20UL, notes.RecordKey.RecordNumber);
        Assert.AreEqual(4096L, notes.Size);
        Assert.IsTrue(notes.IsSizeKnown);
        Assert.AreEqual(Modified, notes.LastWriteTime);
        CollectionAssert.AreEquivalent(new ulong[] { 20, 22 }, index.Search(new SearchQuery("notes.txt"))
            .Select(entry => entry.RecordKey.RecordNumber).ToArray());
        var duplicates = index.DuplicateNames().Single();
        Assert.AreEqual("notes.txt", duplicates.Name);
        Assert.AreEqual(2, duplicates.Entries.Count);
        var deleted = index.Find(At("documents", "obsolete.txt"))!.Value;
        var renamed = index.Find(At("documents", "draft.txt"))!.Value;
        Assert.IsNull(index.Drives[0].CheckpointLoss);
        var block = index.Root('C').DriveBlock.Block;
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.IsNull(index.Find(At("documents", "created.txt")));

        var changes = index.ApplyJournalEntries('C', CatchUpEntries(), AdvancedCursor.JournalId, AdvancedCursor.NextUsn);

        CollectionAssert.AreEqual(new[] { FileChangeKind.Created, FileChangeKind.Deleted, FileChangeKind.Renamed },
            changes.Select(change => change.Kind).ToArray());
        Assert.AreEqual(30UL, index.Find(At("documents", "created.txt"))!.Value.RecordKey.RecordNumber);
        Assert.IsTrue(deleted.IsDeleted);
        Assert.IsTrue(block.Rows[21].IsDeleted);
        Assert.IsNull(index.Find(At("documents", "obsolete.txt")));
        Assert.AreEqual("published.txt", renamed.Name);
        Assert.AreEqual(23UL, index.Find(At("documents", "published.txt"))!.Value.RecordKey.RecordNumber);
        Assert.IsNull(index.Find(At("documents", "draft.txt")));
        Assert.AreEqual(AdvancedCursor.JournalId, block.Header.UsnJournalId);
        Assert.AreEqual(AdvancedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.IsTrue(block.Header.UsnNextUsn > ArmedCursor.NextUsn);
    }

    [TestMethod]
    public async Task OpenAsync_BlockProgressAndDirectoryProfileSurviveTheProtocol()
    {
        var parsingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<BrokerScanProgress>();
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, reporter, _, cancellationToken) =>
        {
            reporter?.Report(new BlockWriteProgress(6, 0, 6, null, BrokerScanPhase.Parsing));
            parsingReceived.Task.Wait(HangGuard, cancellationToken);
            return Records();
        }));
        var source = new BrokerMftBlockProducer(Connect(broker.Process), new BrokerScanOptions
        {
            Profile = BrokerScanProfile.DirectoryIndex,
            KeepFileNames = ["NOTES.TXT"],
            Progress = new SynchronousProgress<BrokerScanProgress>(value =>
            {
                progress.Add(value);
                if (value.Phase == BrokerScanPhase.Parsing)
                {
                    parsingReceived.TrySetResult();
                }
            })
        }).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard);

        AssertReady(index);
        Assert.AreEqual(BrokerScanPhase.Parsing, progress[0].Phase);
        Assert.AreEqual(BrokerScanPhase.Transferring, progress[^1].Phase);
        Assert.IsTrue(progress.All(value => value.DriveLetter == "C"));
        Assert.IsTrue(progress[^1].BytesProcessed > 0);
        Assert.IsTrue(index.Find(At("documents"))!.Value.IsDirectory);
        Assert.IsNotNull(index.Find(At("documents", "notes.txt")));
        Assert.IsNotNull(index.Find(At("notes.txt")));
        Assert.IsNull(index.Find(At("documents", "obsolete.txt")));
        Assert.IsNull(index.Find(At("documents", "draft.txt")));
    }

    [TestMethod]
    public async Task OpenAsync_RecordBeyondPlannedCapacityMarksDriveStale()
    {
        await using var broker = new InProcessBroker(CreateHost(
            scanDrive: (_, _, _, _, _, _) => Records().Append([Record(1_000_000, 5, "beyond.txt")]),
            queryVolumeInfo: _ => new NtfsVolumeInformation(1024, 1024)));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard);

        var created = broker.Sections.Single().Block;
        Assert.IsTrue(created.Header.SlotCapacity < 1_000_000);
        Assert.IsTrue(created.Header.IsComplete);
        Assert.IsTrue(created.Header.IsCompactionNeeded);
        Assert.IsTrue(index.Drives[0].CompactionNeeded);
        Assert.AreEqual(DriveState.Stale, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf('C').ProducerKind);
        Assert.IsNotNull(index.Find(At("documents", "notes.txt")));
        Assert.IsNull(index.Find(At("beyond.txt")));
    }

    [TestMethod]
    public async Task BlockSession_ReplacesParkedWatchCursorAndIndexRescanPreservesOldHandles()
    {
        // The watch a drive runs is armed from its published block's cursor, not from the cursor
        // catch-up advanced to (FileIndex.WatchCheckpointLoss.cs, BuildWatchTarget). A rescan publishes a
        // block with a fresh armed cursor, and the restarted watch follows it.
        var rearmed = new StrongBox<int>();
        var scanCount = new StrongBox<int>();
        var watchSource = new ScriptedWatchSource();
        await using var broker = new InProcessBroker(CreateHost(
            queryCursor: _ => Volatile.Read(ref rearmed.Value) == 0 ? ArmedCursor : RearmedCursor,
            scanDrive: (_, _, _, _, _, _) => Interlocked.Increment(ref scanCount.Value) == 1
                ? Records()
                : [[Record(5, 5, ".", directory: true), Record(40, 5, "replacement.txt")]],
            readJournal: (_, since, _) => (Array.Empty<UsnJournalEntry>(), Volatile.Read(ref rearmed.Value) == 0 ? AdvancedCursor : since)));
        var producer = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource().Producer;
        await using var index = await FileIndex.OpenAsync(Options(new MftIndexSource(producer, watchSource)),
            CancellationToken.None).WaitAsync(HangGuard);
        var previous = index.Find(At("documents", "notes.txt"))!.Value;
        var previousBlock = previous.DriveBlock.Block;
        var firstBlockCursor = new UsnJournalCursor(previousBlock.Header.UsnJournalId, previousBlock.Header.UsnNextUsn);

        await index.StartWatchingAsync('C', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(ArmedCursor, firstBlockCursor);
        Assert.AreNotEqual(AdvancedCursor, firstBlockCursor, "catch-up advanced past the armed cursor");
        var firstStart = watchSource.Starts.Single();
        Assert.AreEqual(new ScriptedWatchStart('C', firstBlockCursor.ToSynthetic()), firstStart);

        Volatile.Write(ref rearmed.Value, 1);
        await index.RescanAsync('C', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(2, scanCount.Value);
        var replacementBlock = index.Root('C').DriveBlock.Block;
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreNotSame(previousBlock, replacementBlock);
        Assert.AreEqual(RearmedCursor.NextUsn, replacementBlock.Header.UsnNextUsn);
        Assert.AreEqual(40UL, index.Find(At("replacement.txt"))!.Value.RecordKey.RecordNumber);
        Assert.IsNull(index.Find(At("documents", "notes.txt")));
        Assert.AreEqual(2, watchSource.Starts.Count);
        Assert.AreEqual(new ScriptedWatchStart('C', RearmedCursor.ToSynthetic()), watchSource.Starts[1]);
        Assert.AreEqual(Path.Combine(_rootDirectory, "documents", "notes.txt"), previous.Path);
        Assert.AreEqual("notes.txt", previous.Name);
        Assert.AreEqual(4096L, previous.Size);
        Assert.AreEqual(Modified, previous.LastWriteTime);
        Assert.IsFalse(previous.IsDeleted);
    }

    [TestMethod]
    public async Task OpenAsync_CatchUpLossAdoptsCompleteBlockWithTheArmedCursor()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => TrimmedWindow);
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _, _) => Records(),
            readJournal: (_, _, _) => throw new IOException("journal wrapped during scan")));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard);

        var loss = index.Drives[0].CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        var block = index.Root('C').DriveBlock.Block;
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf('C').ProducerKind);
        Assert.IsNotNull(index.Find(At("documents", "notes.txt")));
    }

    string At(params string[] segments) => Path.Combine([_rootDirectory, .. segments]);

    FileIndexOptions Options(MftIndexSource source) => new()
    {
        Drives = [new IndexedDrive('C', _rootDirectory, 123)],
        CacheDirectory = _cacheDirectory,
        ProducerPolicy = ProducerPolicy.Mft,
        MftSource = source
    };

    static void AssertReady(FileIndex index)
    {
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf('C').ProducerKind);
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
                directory ? 0 : 4096, Modified.ToFileTimeUtc()), name);

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
            TimestampUtc = Modified,
            FileAttributes = FileAttributes.Normal
        });
}
