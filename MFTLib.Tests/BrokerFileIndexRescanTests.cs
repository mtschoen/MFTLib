using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A <see cref="FileIndex" /> over volumes <c>T</c> and <c>U</c> on one in-process broker: the
///     index scans and watches every volume through its own pipes, so rescanning one volume
///     reopens that volume's watch channel and touches no other.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public sealed class BrokerFileIndexRescanTests
{
    [TestMethod]
    public async Task RescanOfT_OverBroker_ReopensOnlyTsChannel()
    {
        using var scenario = new BrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource();
        await using var indexT = await scenario.OpenIndexAsync('T', source.Producer, source.WatchSource!, token);
        await using var indexU = await scenario.OpenIndexAsync('U', source.Producer, source.WatchSource!, token);
        var set = new FileIndexSet([indexT, indexU]);
        await indexT.StartWatchingAsync(token);
        await indexU.StartWatchingAsync(token);
        var firstT = await harness.Watch('T').RunAsync(1);
        var firstU = await harness.Watch('U').RunAsync(1);
        var scanning = scenario.HoldSecondScanOfT();

        var rescan = indexT.RescanAsync(token);
        await scanning.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        try
        {
            Assert.IsFalse(firstT.Cancelled.IsCompleted, "T's scan and watch channels coexist during production");
            var appliedDuringScan = ChangeSignal.WhenApplied(indexT, "t-during.txt");
            firstT.Push(42, "t-during.txt", 200);
            await appliedDuringScan;
            Assert.HasCount(1, set.Search(new SearchQuery("t-during.txt", NameMatchMode.Exact), token));
            var appliedOnU = ChangeSignal.WhenApplied(indexU, "during.txt");
            firstU.Push(40, "during.txt", 200);
            await appliedOnU;
            Assert.IsFalse(firstU.Cancelled.IsCompleted, "U's channel stays open through T's rescan");
            Assert.IsFalse(rescan.IsCompleted, "the rescan is held inside T's scan");
        }
        finally
        {
            scanning.Release();
        }
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await firstT.Cancelled.WaitAsync(HangGuard, TestContext.CancellationToken);

        var secondT = await harness.Watch('T').RunAsync(2);
        Assert.AreEqual(2, harness.Watch('T').StartedCount);
        Assert.AreEqual(1, harness.Watch('U').StartedCount, "U's channel was never reopened");
        Assert.AreEqual(ScriptedWatchBrokerHarness.DefaultTip, secondT.Since, "T reopens from its fresh block's cursor");
        Assert.HasCount(1, set.Search(new SearchQuery("scan-2.txt", NameMatchMode.Exact), token));
        Assert.HasCount(1, set.Search(new SearchQuery("scan-1.txt", NameMatchMode.Exact), token), "only U's first scan is left");
        Assert.IsNull(indexT.Status.Watch.FailureMessage);
        Assert.IsNull(indexU.Status.Watch.FailureMessage);
        var appliedOnT = ChangeSignal.WhenApplied(indexT, "after.txt");
        secondT.Push(41, "after.txt", 300);
        await appliedOnT;
        await indexT.StopWatchingAsync(token);
        await indexU.StopWatchingAsync(token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WaitsForTheBrokerConnection_AndARescanNeedsNoDeliveredItem()
    {
        using var scenario = new BrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var connect = harness.ConnectAsync;
        var connectGate = new TestGate();
        var source = new BrokerIndexWatchSource(async connectToken =>
        {
            connectGate.MarkEntered();
            await connectGate.WaitForReleaseAsync(connectToken);
            return await connect(connectToken);
        });
        try
        {
            await using var index = await scenario.OpenIndexAsync('T', producer.CreateIndexSource().Producer, source, token);

            var start = index.StartWatchingAsync(token);
            await connectGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsFalse(start.IsCompleted, "StartWatchingAsync completed before the broker connected.");

            connectGate.Release();
            await start.WaitAsync(HangGuard, TestContext.CancellationToken);
            await index.RescanAsync(token);

            Assert.IsNull(index.Status.Watch.FailureMessage);
            await index.StopWatchingAsync(token);
        }
        finally
        {
            connectGate.Release();
        }
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheBrokerConnectionFails_ReportsFailureAndLeavesTheIndexStartable()
    {
        using var scenario = new BrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var connect = harness.ConnectAsync;
        var connectionFailure = new IOException("the broker pipe is gone");
        var connectAttempts = 0;
        var source = new BrokerIndexWatchSource(connectToken => Interlocked.Increment(ref connectAttempts) == 1
            ? Task.FromException<BrokerProcess>(connectionFailure)
            : connect(connectToken));
        await using var index = await scenario.OpenIndexAsync('T', producer.CreateIndexSource().Producer, source, token);

        var failure = await RootResultAssertions.FailureAsync<IOException>(() => index.StartWatchingAsync(token));

        Assert.AreSame(connectionFailure, failure);
        Assert.AreEqual(WatchState.Faulted, index.Status.Watch.State);
        await index.StopWatchingAsync(token);
        await index.StartWatchingAsync(token);
        await index.RescanAsync(token);
        Assert.IsNull(index.Status.Watch.FailureMessage);
        await index.StopWatchingAsync(token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_CancelledWhileTheBrokerConnects_ThrowsAndLeavesTheIndexStartable()
    {
        using var scenario = new BrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var connect = harness.ConnectAsync;
        var connectGate = new TestGate();
        var connectAttempts = 0;
        var source = new BrokerIndexWatchSource(async connectToken =>
        {
            if (Interlocked.Increment(ref connectAttempts) == 1)
            {
                connectGate.MarkEntered();
                await connectGate.WaitForReleaseAsync(connectToken);
            }

            return await connect(connectToken);
        });
        try
        {
            await using var index = await scenario.OpenIndexAsync('T', producer.CreateIndexSource().Producer, source, token);
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

            var start = index.StartWatchingAsync(startCancellation.Token);
            await connectGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            await startCancellation.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard, TestContext.CancellationToken));
            await index.StartWatchingAsync(token);
            await index.RescanAsync(token);
            Assert.IsNull(index.Status.Watch.FailureMessage);
            await index.StopWatchingAsync(token);
        }
        finally
        {
            connectGate.Release();
        }
    }

    /// <summary>
    ///     Volumes <c>T</c> and <c>U</c> scanned by the in-process host, each scan naming its one file
    ///     after the volume's scan number, so a test can tell which scan produced a block.
    /// </summary>
    sealed class BrokerScenario : IDisposable
    {
        readonly TempDirectory _cache = new("broker-rescan", create: false);
        readonly TempDirectory _roots = new("broker-rescan-roots");
        readonly ConcurrentDictionary<VolumeKey, int> _scans = new();
        TestGate? _heldScanOfT;

        public void Dispose()
        {
            _heldScanOfT?.Release();
            try
            {
                _cache.Dispose();
            }
            finally
            {
                _roots.Dispose();
            }
        }

        public ScriptedWatchBrokerHarness CreateHarness() => new(scanVolume: Scan);

        /// <summary>Parks T's second scan inside the host until the returned gate is released.</summary>
        public TestGate HoldSecondScanOfT() => _heldScanOfT = new TestGate();

        public Task<FileIndex> OpenIndexAsync(char drive, MftBlockProducer producer, IIndexWatchSource watchSource,
            CancellationToken token)
        {
            return FileIndex.OpenAsync(new FileIndexOptions
            {
                Root = SyntheticIndexedRoot.Create(new VolumeKey(drive), _roots.Path, drive == 'T' ? 123u : 456u),
                CacheDirectory = _cache.Path,
                CacheMode = CacheMode.Disabled,
                MftSource = new MftIndexSource(producer, watchSource)
            }, token);
        }

        IEnumerable<IReadOnlyList<MftRecord>> Scan(VolumeKey volume, ParseThreadAllowance parseThreads,
            BlockWriteReporting reporting,
            MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken)
        {
            var scanNumber = _scans.AddOrUpdate(volume, 1, (_, count) => count + 1);
            if (volume == new VolumeKey('T') && scanNumber == 2 && _heldScanOfT is { } held)
            {
                held.MarkEntered();
                held.WaitForRelease();
            }

            yield return [new MftRecord(5, 5, new MftRecordFields(3), "."),
                new MftRecord(20, 5, new MftRecordFields(1), $"scan-{scanNumber}.txt")];
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
