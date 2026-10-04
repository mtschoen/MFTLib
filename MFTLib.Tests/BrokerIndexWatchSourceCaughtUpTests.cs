using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerIndexWatchSourceCaughtUpTests
{
    [TestMethod]
    public async Task WatchSource_SurfacesTheMarkerOnlyOnceTheBacklogReachesTheTip()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        await using var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 50), token);
        var reader = handle.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = reader.ConfigureAwait(false);
        var run = await harness.Watch('C').RunAsync(1);

        run.Push(1, "backlog.txt", 80);
        Assert.AreEqual(80L, (await WatchReads.NextBatchAsync(reader)).NextUsn);
        run.Push(2, "tip.txt", ScriptedWatchBrokerHarness.DefaultTip.NextUsn);

        Assert.AreEqual(ScriptedWatchBrokerHarness.DefaultTip.NextUsn, (await WatchReads.NextBatchAsync(reader)).NextUsn);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
    }

    [TestMethod]
    public async Task WatchSource_AfterARestart_SurfacesOnlyTheFreshWatchsMarker()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        var first = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var firstReader = first.ReadAsync(token).GetAsyncEnumerator(token);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(firstReader));
        await firstReader.DisposeAsync();
        await first.DisposeAsync();

        // The fresh watch starts behind the tip: no marker until its own backlog is delivered,
        // and the first watch's marker is not delivered again.
        await using var restarted = await source.StartAsync(new IndexWatchTarget('C', 7, 50), token);
        var reader = restarted.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = reader.ConfigureAwait(false);
        var run = await harness.Watch('C').RunAsync(2);
        run.Push(1, "backlog.txt", 80);
        run.Push(2, "tip.txt", ScriptedWatchBrokerHarness.DefaultTip.NextUsn);
        run.Push(3, "live.txt", 200);

        Assert.AreEqual(80L, (await WatchReads.NextBatchAsync(reader)).NextUsn);
        Assert.AreEqual(ScriptedWatchBrokerHarness.DefaultTip.NextUsn, (await WatchReads.NextBatchAsync(reader)).NextUsn);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
        Assert.AreEqual(200L, (await WatchReads.NextBatchAsync(reader)).NextUsn);
    }
}
