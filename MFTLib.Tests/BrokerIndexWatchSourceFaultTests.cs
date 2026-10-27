using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The broker-side half of per-volume fault isolation: each volume's watch is its own pipe, so a
///     fault or a lost pipe on one volume reaches that volume's handle and no other.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerIndexWatchSourceFaultTests
{
    [TestMethod]
    public async Task WatchSource_OneVolumesFaultIsThatHandlesAndTheOtherVolumeKeepsFlowing()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        await using var handleC = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        await using var handleD = await source.StartAsync(new IndexWatchTarget('D', 7, 100), token);
        var readerC = handleC.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = readerC.ConfigureAwait(false);
        var readerD = handleD.ReadAsync(token).GetAsyncEnumerator(token);
        await using var __ = readerD.ConfigureAwait(false);
        var runC = await harness.Watch('C').RunAsync(1);
        var runD = await harness.Watch('D').RunAsync(1);
        Assert.IsInstanceOfType<VolumeCaughtUp>(await WatchReads.NextAsync(readerC));
        Assert.IsInstanceOfType<VolumeCaughtUp>(await WatchReads.NextAsync(readerD));

        runD.Fail(new IOException("journal wrapped"));

        var fault = await WatchReads.ThrowsNextAsync<VolumeWatchFaultException>(readerD);
        Assert.IsNull(fault.InnerException);
        Assert.AreEqual("journal wrapped", fault.Message);
        runC.Push(1, "c.txt", 110);
        var batch = await WatchReads.NextBatchAsync(readerC);
        Assert.AreEqual(7ul, batch.JournalIdentifier);
        Assert.AreEqual(110L, batch.NextUsn);
        Assert.AreEqual("c.txt", batch.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task WatchSource_EveryVolumeFaultsOnItsOwnHandle()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        await using var handleC = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        await using var handleD = await source.StartAsync(new IndexWatchTarget('D', 7, 100), token);
        var readerC = handleC.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = readerC.ConfigureAwait(false);
        var readerD = handleD.ReadAsync(token).GetAsyncEnumerator(token);
        await using var __ = readerD.ConfigureAwait(false);
        (await harness.Watch('C').RunAsync(1)).Fail(new IOException("C wrapped"));
        (await harness.Watch('D').RunAsync(1)).Fail(new IOException("D wrapped"));
        Assert.IsInstanceOfType<VolumeCaughtUp>(await WatchReads.NextAsync(readerC));
        Assert.IsInstanceOfType<VolumeCaughtUp>(await WatchReads.NextAsync(readerD));

        var faultC = await WatchReads.ThrowsNextAsync<VolumeWatchFaultException>(readerC);
        var faultD = await WatchReads.ThrowsNextAsync<VolumeWatchFaultException>(readerD);

        Assert.AreEqual("C wrapped", faultC.Message);
        Assert.AreEqual("D wrapped", faultD.Message);
    }

    [TestMethod]
    public async Task WatchSource_StartFailsWhenItCannotConnect()
    {
        var connectFailure = new IOException("the broker never launched");
        var source = new BrokerIndexWatchSource(_ => throw connectFailure);

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            source.StartAsync(new IndexWatchTarget('C', 7, 100), CancellationToken.None));

        Assert.AreSame(connectFailure, thrown);
    }

    [TestMethod]
    public async Task OneChannelLost_OnlyThatVolumeFaults()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerMftBlockProducer(broker.ConnectAsync).CreateIndexSource().WatchSource!;
        using var harness = new WatchHarness(source, 'T', 'U');
        var indexT = harness.IndexFor('T');
        var indexU = harness.IndexFor('U');
        await indexT.StartWatchingAsync(token);
        await indexU.StartWatchingAsync(token);
        var runT = await broker.Watch('T').RunAsync(1);
        var runU = await broker.Watch('U').RunAsync(1);
        await Task.WhenAll(indexT.WaitForCatchUpAsync(token), indexU.WaitForCatchUpAsync(token))
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        // The host closes T's pipe: its watch ends without a word.
        runT.End();
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        var appliedOnU = ChangeSignal.WhenApplied(indexU, "u.txt");
        runU.Push(10, "u.txt", 300);
        await appliedOnU;

        Assert.IsInstanceOfType<BrokerChannelLostException>(fault.Exception);
        Assert.AreEqual('T', ((BrokerChannelLostException)fault.Exception).Volume);
        Assert.HasCount(1, harness.Faults);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
        Assert.IsNotNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('U').Watch.State);
        Assert.IsNull(harness.RootFor('U').Watch.FailureMessage);
        Assert.IsFalse(broker.Process.Ended.IsCompleted, "one lost volume channel does not end the process");
    }

    [TestMethod]
    public async Task HostError_IsVolumeWatchFault()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerMftBlockProducer(broker.ConnectAsync).CreateIndexSource().WatchSource!;
        using var harness = new WatchHarness(source, 'T', 'U');
        var indexT = harness.IndexFor('T');
        var indexU = harness.IndexFor('U');
        await indexT.StartWatchingAsync(token);
        await indexU.StartWatchingAsync(token);
        var runU = await broker.Watch('U').RunAsync(1);

        (await broker.Watch('T').RunAsync(1)).Fail(new IOException("journal wrapped"));

        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        Assert.IsInstanceOfType<VolumeWatchFaultException>(fault.Exception);
        Assert.AreEqual('T', fault.Volume);
        Assert.AreEqual("journal wrapped", fault.Exception.Message);
        var appliedOnU = ChangeSignal.WhenApplied(indexU, "u.txt");
        runU.Push(10, "u.txt", 300);
        await appliedOnU;
        Assert.IsTrue(harness.Faults.All(raised => raised.Volume == 'T'), "only T raised a fault");
    }

    public TestContext TestContext { get; set; } = null!;
}
