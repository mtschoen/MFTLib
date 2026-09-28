using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class ConsumerJournalIsolationTests
{
    readonly string _directory = Path.Combine(Path.GetTempPath(),
        $"mftlib-consumer-journal-{Guid.NewGuid():N}");
    int _productions;

    public TestContext TestContext { get; set; } = null!;
    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static SyntheticJournalWindow Healthy => new(7, 0, 5_000, 64, 8_192);
    static SyntheticJournalWindow Lost(bool recreated) => recreated
        ? new(8, 0, 5_000, 64, 8_192)
        : new(7, 1_500, 5_000, 64, 8_192);

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(_directory);
        JournalIsolation.ForbidLiveJournalReads();
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    FileIndexOptions Options(bool cacheOnly, IIndexWatchSource? source = null) => new()
    {
        Drives = [new IndexedDrive('T', _directory, 0xBADF00D)],
        CacheDirectory = Path.Combine(_directory, "cache"),
        ProducerPolicy = ProducerPolicy.Mft,
        MftProducer = Produce,
        InitialOpenCacheOnly = cacheOnly,
        WatchSource = source
    };

    Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _productions);
        using (var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose,
            CacheTag = request.CacheTag
        }))
        {
            var writer = new BlockWriter(block);
            Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(
                ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory,
                Attributes: 0, Size: 0, ModifiedTicks: 0, SequenceNumber: 0)));
            writer.SetJournalCursor(7, 1_000);
            writer.Complete(DateTime.UtcNow);
        }

        return Task.FromResult(new MftBlockProduceResult(
            BlockFile.Open(request.BlockPath, request.VolumeSerial, out _) ?? throw new InvalidOperationException("Failed to reopen produced block"),
            7, 1_000,
            SkippedRecordCount: 0, CompactionNeeded: false));
    }

    async Task SeedAsync()
    {
        await using var index = await FileIndex.OpenAsync(Options(false), Token);
        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
        Assert.AreEqual(1, _productions);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LostWindow_ReachesRealOpenAndCacheOnlyRefusal(bool recreated, bool cacheOnly)
    {
        var windows = new ConcurrentDictionary<char, SyntheticJournalWindow>();
        windows['T'] = Healthy;
        using var scope = JournalIsolation.OverrideJournalWindow(
            drive => windows.TryGetValue(drive, out var window) ? window : null);
        await SeedAsync();
        windows['T'] = Lost(recreated);
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(Options(cacheOnly, source), Token);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(cacheOnly ? 1 : 2, _productions);
        Assert.AreEqual(cacheOnly ? BlockSource.WarmStartedFromCache : BlockSource.ProducedByScan,
            status.BlockSource);
        Assert.IsNotNull(status.CheckpointLoss);
        Assert.AreEqual(recreated ? JournalCheckpointLossCause.JournalRecreated
            : JournalCheckpointLossCause.CheckpointTrimmed, status.CheckpointLoss.Cause);
        Assert.AreEqual(JournalCheckpointLossDetection.DriveOpening,
            status.CheckpointLoss.DetectedDuring);
        Assert.AreEqual(1_000L, status.CheckpointLoss.CheckpointUsn);
        Assert.IsNull(status.WatchFailureMessage);

        if (cacheOnly)
        {
            await index.StartWatchingAsync(Token);
            Assert.AreEqual(0, source.SourceInvocationCount);
            status = index.Drives.Single();
            Assert.IsNotNull(status.WatchFailureMessage);
            StringAssert.Contains(status.WatchFailureMessage, "RescanAsync");
            Assert.AreEqual(WatchCatchUpState.Faulted, status.WatchCatchUp);
            await index.StopWatchingAsync(Token);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetainedOrUnknownWindow_WarmStartsAndArms(bool unknown)
    {
        using var scope = JournalIsolation.OverrideJournalWindow(_ => unknown ? null : Healthy);
        await SeedAsync();
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(Options(true, source), Token);
        Assert.AreEqual(1, _productions);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, index.Drives.Single().BlockSource);
        Assert.IsNull(index.Drives.Single().CheckpointLoss);
        await index.StartWatchingAsync(Token);
        var targets = await source.SourceStartedAsync();
        Assert.AreEqual('T', targets.Single().DriveLetter);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, index.Drives.Single().WatchCatchUp);
        Assert.IsNull(index.Drives.Single().WatchFailureMessage);
        await index.StopWatchingAsync(Token);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task WatchFault_UsesLatestSyntheticWindowAndAppliedCursor(int observation)
    {
        var windows = new ConcurrentDictionary<char, SyntheticJournalWindow>();
        windows['T'] = Healthy;
        using var scope = JournalIsolation.OverrideJournalWindow(
            drive => windows.TryGetValue(drive, out var window) ? window : null);
        await SeedAsync();
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(Options(false, source), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();
        await source.PublishAsync(new JournalBatch('T', [], JournalId: 7, NextUsn: 2_000));
        windows['T'] = observation switch
        {
            1 => new SyntheticJournalWindow(7, 2_500, 6_000, 64, 8_192),
            2 => new SyntheticJournalWindow(8, 0, 6_000, 64, 8_192),
            _ => Healthy
        };
        await source.PublishAsync(new DriveWatchFailure('T', new IOException("synthetic fault")));
        var status = index.Drives.Single();
        Assert.AreEqual(1, _productions);
        Assert.AreEqual(WatchCatchUpState.Faulted, status.WatchCatchUp);
        Assert.IsNotNull(status.WatchFailureMessage);
        if (observation == 0)
        {
            Assert.IsNull(status.CheckpointLoss);
        }
        else
        {
            Assert.IsNotNull(status.CheckpointLoss);
            Assert.AreEqual(2_000L, status.CheckpointLoss.CheckpointUsn);
            Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch,
                status.CheckpointLoss.DetectedDuring);
            Assert.AreEqual(observation == 1 ? JournalCheckpointLossCause.CheckpointTrimmed
                : JournalCheckpointLossCause.JournalRecreated, status.CheckpointLoss.Cause);
        }
        await Assert.ThrowsExceptionAsync<IOException>(() => index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task ExceptionalScopeExit_RestoresIsolationForNextRealOpen()
    {
        using (JournalIsolation.OverrideJournalWindow(_ => Healthy))
        {
            await SeedAsync();
        }
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
        {
            using var scope = JournalIsolation.OverrideJournalWindow(_ => Lost(false));
            await using var index = await FileIndex.OpenAsync(Options(true), Token);
            Assert.IsNotNull(index.Drives.Single().CheckpointLoss);
            throw new IOException("synthetic test body failure");
        });
        await using var restored = await FileIndex.OpenAsync(Options(true), Token);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, restored.Drives.Single().BlockSource);
        Assert.AreEqual(1, _productions);
        Assert.IsNull(restored.Drives.Single().CheckpointLoss);
    }
}
