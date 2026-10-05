using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A <see cref="FileIndex" /> over drives <c>T</c> and <c>U</c> on one in-process broker: the
///     index scans and watches every drive through its own pipes, so rescanning one drive
///     reopens that drive's watch channel and touches no other.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public sealed class BrokerFileIndexRescanTests
{
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    [TestMethod]
    public async Task RescanOfT_OverBroker_ReopensOnlyTsChannel()
    {
        using var scenario = new BrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource();
        await using var index = await scenario.OpenIndexAsync(source.Producer, source.WatchSource!, token);
        await index.StartWatchingAsync('T', token);
        await index.StartWatchingAsync('U', token);
        var firstT = await harness.Watch('T').RunAsync(1);
        var firstU = await harness.Watch('U').RunAsync(1);
        var scanning = scenario.HoldSecondScanOfT();

        var rescan = index.RescanAsync('T', token);
        await scanning.Entered.WaitAsync(HangGuard);

        try
        {
            Assert.IsFalse(firstT.Cancelled.IsCompleted, "T's scan and watch channels coexist during production");
            var appliedDuringScan = ChangeSignal.WhenApplied(index, "t-during.txt");
            firstT.Push(42, "t-during.txt", 200);
            await appliedDuringScan;
            Assert.AreEqual(1, index.FindByName("t-during.txt", token).Count);
            var appliedOnU = ChangeSignal.WhenApplied(index, "during.txt");
            firstU.Push(40, "during.txt", 200);
            await appliedOnU;
            Assert.IsFalse(firstU.Cancelled.IsCompleted, "U's channel stays open through T's rescan");
            Assert.IsFalse(rescan.IsCompleted, "the rescan is held inside T's scan");
        }
        finally
        {
            scanning.Release();
        }
        await rescan.WaitAsync(HangGuard);
        await firstT.Cancelled.WaitAsync(HangGuard);

        var secondT = await harness.Watch('T').RunAsync(2);
        Assert.AreEqual(2, harness.Watch('T').StartedCount);
        Assert.AreEqual(1, harness.Watch('U').StartedCount, "U's channel was never reopened");
        Assert.AreEqual(ScriptedWatchBrokerHarness.DefaultTip, secondT.Since, "T reopens from its fresh block's cursor");
        Assert.AreEqual(1, index.FindByName("scan-2.txt", token).Count);
        Assert.AreEqual(1, index.FindByName("scan-1.txt", token).Count, "only U's first scan is left");
        Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
        var appliedOnT = ChangeSignal.WhenApplied(index, "after.txt");
        secondT.Push(41, "after.txt", 300);
        await appliedOnT;
        await index.StopWatchingAsync('T', token);
        await index.StopWatchingAsync('U', token);
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
            await using var index = await scenario.OpenIndexAsync(producer.CreateIndexSource().Producer, source, token);

            var start = index.StartWatchingAsync('T', token);
            await connectGate.Entered.WaitAsync(HangGuard);
            Assert.IsFalse(start.IsCompleted, "StartWatchingAsync completed before the broker connected.");

            connectGate.Release();
            await start.WaitAsync(HangGuard);
            await index.RescanAsync('U', token);

            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            await index.StopWatchingAsync('T', token);
        }
        finally
        {
            connectGate.Release();
        }
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheBrokerConnectionFails_ThrowsItAndLeavesTheIndexStartable()
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
        await using var index = await scenario.OpenIndexAsync(producer.CreateIndexSource().Producer, source, token);

        var thrown = await WatchDeduplicationTestSupport.ThrowsAsync<IOException>(() => index.StartWatchingAsync('T', token));

        Assert.AreSame(connectionFailure, thrown);
        Assert.AreEqual(WatchCatchUpState.Faulted, index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
        await index.StopWatchingAsync('T', token);
        await index.StartWatchingAsync('T', token);
        await index.RescanAsync('T', token);
        Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
        await index.StopWatchingAsync('T', token);
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
            await using var index = await scenario.OpenIndexAsync(producer.CreateIndexSource().Producer, source, token);
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

            var start = index.StartWatchingAsync('T', startCancellation.Token);
            await connectGate.Entered.WaitAsync(HangGuard);
            await startCancellation.CancelAsync();

            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard));
            await index.StartWatchingAsync('T', token);
            await index.RescanAsync('T', token);
            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            await index.StopWatchingAsync('T', token);
        }
        finally
        {
            connectGate.Release();
        }
    }

    /// <summary>
    ///     Drives <c>T</c> and <c>U</c> scanned by the in-process host, each scan naming its one file
    ///     after the drive's scan number, so a test can tell which scan produced a block.
    /// </summary>
    sealed class BrokerScenario : IDisposable
    {
        readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"broker-rescan-{Guid.NewGuid():N}");
        readonly ConcurrentDictionary<string, int> _scans = new(StringComparer.Ordinal);
        TestGate? _heldScanOfT;

        public void Dispose()
        {
            _heldScanOfT?.Release();
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }

        public ScriptedWatchBrokerHarness CreateHarness() => new(scanDrive: Scan);

        /// <summary>Parks T's second scan inside the host until the returned gate is released.</summary>
        public TestGate HoldSecondScanOfT() => _heldScanOfT = new TestGate();

        public Task<FileIndex> OpenIndexAsync(MftBlockProducer producer, IIndexWatchSource watchSource,
            CancellationToken token)
        {
            return FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', Path.GetTempPath(), 123), new IndexedDrive('U', Path.GetTempPath(), 456)],
                CacheDirectory = _cacheDirectory,
                NoCache = true,
                MftSource = new MftIndexSource(producer, watchSource)
            }, token);
        }

        IEnumerable<IReadOnlyList<MftRecord>> Scan(string drive, ParseThreadAllowance parseThreads,
            IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
            CancellationToken cancellationToken)
        {
            var scanNumber = _scans.AddOrUpdate(drive, 1, (_, count) => count + 1);
            if (drive == "T" && scanNumber == 2 && _heldScanOfT is { } held)
            {
                held.MarkEntered();
                held.WaitForRelease();
            }

            yield return [new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                new MftRecord(20, 5, new MftRecordFields(1), $"scan-{scanNumber}.txt", null)];
        }
    }
}
