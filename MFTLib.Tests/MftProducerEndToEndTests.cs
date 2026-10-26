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
    static readonly SyntheticJournalWindow TrimmedWindow = new(71, 20000, 30000, 4096, 32768);
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
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanDrive = (_, _, _, _, _, _) => Records(),
            ReadJournal = CatchUpSources.ToTip(AdvancedCursor, CatchUpEntries())
        }));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertReady(index);
        Assert.AreEqual(24u, index.HeaderOf('C').RowCount);
        Assert.AreEqual(_rootDirectory, index.Root('C', TestContext.CancellationToken).Path);
        var notes = index.Find(At("documents", "notes.txt"), TestContext.CancellationToken)!.Value;
        Assert.AreEqual(20UL, notes.RecordKey.RecordNumber);
        Assert.AreEqual(4096L, notes.Size);
        Assert.IsTrue(notes.IsSizeKnown);
        Assert.AreEqual(Modified, notes.LastWriteTime);
        Assert.AreSequenceEqual(new ulong[] { 20, 22 }, [.. index.Search(new SearchQuery("notes.txt"), TestContext.CancellationToken).Select(entry => entry.RecordKey.RecordNumber)], SequenceOrder.InAnyOrder);
        var duplicates = CollectFileRows(index).GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Single(group => group.Count() > 1);
        Assert.AreEqual("notes.txt", duplicates.Key);
        Assert.HasCount(2, duplicates);
        var deleted = index.Find(At("documents", "obsolete.txt"), TestContext.CancellationToken)!.Value;
        var renamed = index.Find(At("documents", "draft.txt"), TestContext.CancellationToken)!.Value;
        Assert.IsNull(index.Drives[0].Watch.CheckpointLoss);
        var block = index.Root('C', TestContext.CancellationToken).DriveBlock.Block;
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.IsNull(index.Find(At("documents", "created.txt"), TestContext.CancellationToken));

        var changes = await index.ApplyJournalEntriesAsync('C', CatchUpEntries(), AdvancedCursor.JournalIdentifier, AdvancedCursor.NextUsn);

        Assert.AreSequenceEqual([FileChangeKind.Created, FileChangeKind.Deleted, FileChangeKind.Renamed], [.. changes.Select(change => change.Kind)]);
        Assert.AreEqual(30UL, index.Find(At("documents", "created.txt"), TestContext.CancellationToken)!.Value.RecordKey.RecordNumber);
        Assert.IsTrue(deleted.IsDeleted);
        Assert.IsTrue(block.Rows[21].IsDeleted);
        Assert.IsNull(index.Find(At("documents", "obsolete.txt"), TestContext.CancellationToken));
        Assert.AreEqual("published.txt", renamed.Name);
        Assert.AreEqual(23UL, index.Find(At("documents", "published.txt"), TestContext.CancellationToken)!.Value.RecordKey.RecordNumber);
        Assert.IsNull(index.Find(At("documents", "draft.txt"), TestContext.CancellationToken));
        Assert.AreEqual(AdvancedCursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(AdvancedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.IsGreaterThan(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task OpenAsync_BlockProgressAndRetainedFileNamesSurviveTheProtocol()
    {
        var parsingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<IndexScanProgress>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanDrive = (_, _, reporting, _, _, cancellationToken) =>
            {
                reporting.Progress?.Report(new BlockWriteProgress(6, 0, 6, null, BlockScanPhase.Parsing));
                parsingReceived.Task.Wait(HangGuard, cancellationToken);
                return Records();
            }
        }));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        var options = Options(source) with
        {
            RetainedFileNames = ["NOTES.TXT"],
            ScanProgress = new SynchronousProgress<IndexScanProgress>(value =>
            {
                progress.Add(value);
                if (value.Phase == IndexScanPhase.ParsingMft)
                {
                    parsingReceived.TrySetResult();
                }
            })
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertReady(index);
        Assert.Contains(value => value.Phase == IndexScanPhase.ParsingMft, progress);
        Assert.Contains(value => value.Phase == IndexScanPhase.Transferring, progress);
        Assert.IsTrue(progress.All(value => value.Drive == 'C'));
        var transferring = progress.FindLast(value => value.Phase == IndexScanPhase.Transferring);
        Assert.IsNotNull(transferring);
        Assert.IsNotNull(transferring.ExpectedRowCount);
        Assert.IsTrue(transferring.ExpectedRowCount > 0);
        Assert.IsGreaterThan(0u, progress[^1].WrittenRowCount);
        Assert.IsTrue(index.Find(At("documents"), TestContext.CancellationToken)!.Value.IsDirectory);
        Assert.IsNotNull(index.Find(At("documents", "notes.txt"), TestContext.CancellationToken));
        Assert.IsNotNull(index.Find(At("notes.txt"), TestContext.CancellationToken));
        Assert.IsNull(index.Find(At("documents", "obsolete.txt"), TestContext.CancellationToken));
        Assert.IsNull(index.Find(At("documents", "draft.txt"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task OpenAsync_RecordBeyondPlannedCapacityReportsReadyWithCapacityExhausted()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => Records().Append([Record(1_000_000, 5, "beyond.txt")]),
            QueryVolumeInformation = _ => new NtfsVolumeInformation(1024, 1024)
        }));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        var created = broker.Sections.Single().Block;
        Assert.IsLessThan(1_000_000u, created.Header.SlotCapacity);
        Assert.IsTrue(created.Header.IsComplete);
        Assert.IsTrue(created.Header.IsCapacityExhausted);
        Assert.IsTrue(index.Drives[0].Block!.IsCapacityExhausted);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf('C').ProducerKind);
        Assert.IsNotNull(index.Find(At("documents", "notes.txt"), TestContext.CancellationToken));
        Assert.IsNull(index.Find(At("beyond.txt"), TestContext.CancellationToken));
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
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryVolumeInformation = _ => VolumeInformation,
            QueryCursor = _ => Volatile.Read(ref rearmed.Value) == 0 ? ArmedCursor : RearmedCursor,
            ScanDrive = (_, _, _, _, _, _) => Interlocked.Increment(ref scanCount.Value) == 1
                ? Records()
                : [[Record(5, 5, ".", directory: true), Record(40, 5, "replacement.txt")]],
            ReadJournal = (_, since, _) => ([], Volatile.Read(ref rearmed.Value) == 0 ? AdvancedCursor : since)
        }));
        var producer = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource().Producer;
        await using var index = await FileIndex.OpenAsync(Options(new MftIndexSource(producer, watchSource.IndexSource)),
            CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        var previous = index.Find(At("documents", "notes.txt"), TestContext.CancellationToken)!.Value;
        var previousBlock = previous.DriveBlock.Block;
        var firstBlockCursor = new UsnJournalCursor(previousBlock.Header.UsnJournalIdentifier, previousBlock.Header.UsnNextUsn);

        await index.StartWatchingAsync(['C'], CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(ArmedCursor, firstBlockCursor);
        Assert.AreNotEqual(AdvancedCursor, firstBlockCursor, "catch-up advanced past the armed cursor");
        var firstStart = watchSource.Starts.Single();
        Assert.AreEqual(new ScriptedWatchStart('C', firstBlockCursor.ToSynthetic()), firstStart);

        Volatile.Write(ref rearmed.Value, 1);
        await index.RescanAsync(['C'], CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(2, scanCount.Value);
        var replacementBlock = index.Root('C', TestContext.CancellationToken).DriveBlock.Block;
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreNotSame(previousBlock, replacementBlock);
        Assert.AreEqual(RearmedCursor.NextUsn, replacementBlock.Header.UsnNextUsn);
        Assert.AreEqual(40UL, index.Find(At("replacement.txt"), TestContext.CancellationToken)!.Value.RecordKey.RecordNumber);
        Assert.IsNull(index.Find(At("documents", "notes.txt"), TestContext.CancellationToken));
        Assert.HasCount(2, watchSource.Starts);
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
        using var journal = JournalIsolation.OverrideJournalWindow(_ => TrimmedWindow);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanDrive = (_, _, _, _, _, _) => Records(),
            ReadJournal = (_, _, _) => throw new IOException("journal wrapped during scan")
        }));
        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        var loss = index.Drives[0].Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        var block = index.Root('C', TestContext.CancellationToken).DriveBlock.Block;
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf('C').ProducerKind);
        Assert.IsNotNull(index.Find(At("documents", "notes.txt"), TestContext.CancellationToken));
    }

    static List<(string Name, FileEntry Entry)> CollectFileRows(FileIndex index)
    {
        var rows = new List<(string Name, FileEntry Entry)>();
        foreach (var row in index.EnumerateRows(new SearchQuery(null, IsDirectory: false)))
        {
            rows.Add((row.Name.ToString(), row.ToEntry()));
        }

        return rows;
    }

    string At(params string[] segments) => Path.Combine([_rootDirectory, .. segments]);

    FileIndexOptions Options(MftIndexSource source) => new()
    {
        Drives = [new IndexedDrive('C', _rootDirectory, 123)],
        CacheDirectory = _cacheDirectory,
        MftSource = source
    };

    static void AssertReady(FileIndex index)
    {
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf('C').ProducerKind);
        Assert.IsTrue(index.Drives[0].Watch.IsSupported);
        Assert.IsFalse(index.Drives[0].Block!.IsCapacityExhausted);
        Assert.IsNull(index.Drives[0].Failure);
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

    public TestContext TestContext { get; set; } = null!;
}
