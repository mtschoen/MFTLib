using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The consumer-facing journal isolation override drives open-time and watch-fault checks.
///     The override is process-global, so the fixture is nonparallel, scopes never nest, and every
///     index is stopped and disposed before its scope. A drive's pump asks about its own drive,
///     so the window callback can run on a pump thread; the one used here is a concurrent
///     dictionary.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConsumerJournalIsolationTests
{
    readonly string _directory = Path.Combine(Path.GetTempPath(),
        $"mftlib-consumer-journal-{Guid.NewGuid():N}");
    int _productions;

    public TestContext TestContext { get; set; } = null!;
    CancellationToken Token => TestContext.CancellationToken;

    static SyntheticJournalWindow Healthy => new(JournalIdentifier: 7, FirstUsn: 0, NextUsn: 5_000,
        AllocationDelta: 64, MaximumSize: 8_192);
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
        MftSource = new MftIndexSource(Produce, source),
        CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent
    };

    Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _productions);
        using (var block = TestBlocks.Create(request.BlockPath, ProducerKind.Mft,
            BlockLayout.ComputeSlotCapacity(8), BlockLayout.ComputeNamePoolCapacity(256),
            volumeSerial: request.VolumeSerial, rootRow: 5,
            deleteOnClose: request.DeleteOnClose, cacheTag: request.CacheTag,
            scanShape: request.ScanShape))
        {
            var writer = new BlockWriter(block);
            Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(
                ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory,
                Attributes: 0, Size: 0, ModifiedTicks: 0, SequenceNumber: 0)));
            writer.SetJournalCursor(7, 1_000);
            writer.Complete(DateTime.UtcNow, null);
        }

        return Task.FromResult(new MftBlockProduceResult(
            BlockFile.Open(request.BlockPath, request.VolumeSerial, out _) ?? throw new InvalidOperationException("Failed to reopen produced block"),
            SkippedRecordCount: 0));
    }

    async Task SeedAsync()
    {
        await using var index = await FileIndex.OpenAsync(Options(false), Token);
        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Drives.Single().Block!.Origin);
        Assert.AreEqual(1, _productions);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LostWindow_ReachesRealOpenAndCacheOnlyRefusal(bool recreated, bool cacheOnly)
    {
        var windows = new ConcurrentDictionary<char, SyntheticJournalWindow>
        {
            ['T'] = Healthy
        };
        using var scope = JournalIsolation.OverrideJournalWindow(
            drive => windows.TryGetValue(drive.Letter, out var window) ? window : null);
        await SeedAsync();
        windows['T'] = Lost(recreated);
        var source = new ScriptedWatchSource();
        await using var index = await FileIndex.OpenAsync(Options(cacheOnly, source.IndexSource), Token);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(cacheOnly ? 1 : 2, _productions);
        Assert.AreEqual(cacheOnly ? BlockOrigin.WarmStartedFromCache : BlockOrigin.ProducedByScan,
            status.Block!.Origin);
        Assert.IsNotNull(status.Watch.CheckpointLoss);
        Assert.AreEqual(recreated ? JournalCheckpointLossCause.JournalRecreated
            : JournalCheckpointLossCause.CheckpointTrimmed, status.Watch.CheckpointLoss.Cause);
        Assert.AreEqual(JournalCheckpointLossDetection.DriveOpening,
            status.Watch.CheckpointLoss.DetectedDuring);
        Assert.AreEqual(1_000L, status.Watch.CheckpointLoss.CheckpointUsn);
        Assert.IsNull(status.Watch.FailureMessage);

        if (cacheOnly)
        {
            var refusal = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.StartWatchingAsync(['T'], Token));
            Assert.Contains("RescanAsync", refusal.Message);
            Assert.IsEmpty(source.Starts);
            status = index.Drives.Single();
            Assert.IsNotNull(status.Watch.FailureMessage);
            Assert.Contains("RescanAsync", status.Watch.FailureMessage);
            Assert.AreEqual(WatchState.Faulted, status.Watch.State);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetainedOrUnknownWindow_WarmStartsAndArms(bool unknown)
    {
        using var scope = JournalIsolation.OverrideJournalWindow(_ => unknown ? null : Healthy);
        await SeedAsync();
        var source = new ScriptedWatchSource();
        await using var index = await FileIndex.OpenAsync(Options(true, source.IndexSource), Token);
        Assert.AreEqual(1, _productions);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, index.Drives.Single().Block!.Origin);
        Assert.IsNull(index.Drives.Single().Watch.CheckpointLoss);
        await index.StartWatchingAsync(['T'], Token);
        Assert.AreEqual('T', source.Starts.Single().Drive);
        Assert.AreEqual(WatchState.CatchingUp, index.Drives.Single().Watch.State);
        Assert.IsNull(index.Drives.Single().Watch.FailureMessage);
        await index.StopWatchingAsync(['T'], Token);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task WatchFault_UsesLatestSyntheticWindowAndAppliedCursor(int observation)
    {
        var windows = new ConcurrentDictionary<char, SyntheticJournalWindow>
        {
            ['T'] = Healthy
        };
        using var scope = JournalIsolation.OverrideJournalWindow(
            drive => windows.TryGetValue(drive.Letter, out var window) ? window : null);
        await SeedAsync();
        var source = new ScriptedWatchSource();
        await using var index = await FileIndex.OpenAsync(Options(false, source.IndexSource), Token);
        index.HoldEveryRecovery();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += _ => announced.TrySetResult();
        await index.StartWatchingAsync(['T'], Token);
        var handle = source.WatchFor('T');
        await handle.Publish(new JournalBatch([], JournalIdentifier: 7, NextUsn: 2_000));
        windows['T'] = observation switch
        {
            1 => new SyntheticJournalWindow(7, 2_500, 6_000, 64, 8_192),
            2 => new SyntheticJournalWindow(8, 0, 6_000, 64, 8_192),
            _ => Healthy
        };
        handle.FailDrive(new IOException("synthetic fault"));
        await announced.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        var status = index.Drives.Single();
        Assert.AreEqual(1, _productions);
        Assert.AreEqual(WatchState.Recovering, status.Watch.State);
        Assert.IsNotNull(status.Watch.FailureMessage);
        if (observation == 0)
        {
            Assert.IsNull(status.Watch.CheckpointLoss);
        }
        else
        {
            Assert.IsNotNull(status.Watch.CheckpointLoss);
            Assert.AreEqual(2_000L, status.Watch.CheckpointLoss.CheckpointUsn);
            Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch,
                status.Watch.CheckpointLoss.DetectedDuring);
            Assert.AreEqual(observation == 1 ? JournalCheckpointLossCause.CheckpointTrimmed
                : JournalCheckpointLossCause.JournalRecreated, status.Watch.CheckpointLoss.Cause);
        }
        await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => index.StopWatchingAsync(['T'], Token));
    }

    [TestMethod]
    public async Task ScopeEnd_RestoresIsolationForNextRealOpen()
    {
        using (JournalIsolation.OverrideJournalWindow(_ => Healthy))
        {
            await SeedAsync();
        }

        using (JournalIsolation.OverrideJournalWindow(_ => Lost(false)))
        {
            await using var index = await FileIndex.OpenAsync(Options(true), Token);
            Assert.IsNotNull(index.Drives.Single().Watch.CheckpointLoss);
        }

        await using var restored = await FileIndex.OpenAsync(Options(true), Token);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, restored.Drives.Single().Block!.Origin);
        Assert.AreEqual(1, _productions);
        Assert.IsNull(restored.Drives.Single().Watch.CheckpointLoss);
    }
}
