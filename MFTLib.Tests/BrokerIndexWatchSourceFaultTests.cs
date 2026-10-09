using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The broker-side half of per-drive fault isolation: each drive's watch is its own pipe, so a
///     fault or a lost pipe on one drive reaches that drive's handle and no other.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerIndexWatchSourceFaultTests
{
    [TestMethod]
    public async Task WatchSource_OneDrivesFaultIsThatHandlesAndTheOtherDriveKeepsFlowing()
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
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerC));
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerD));

        runD.Fail(new IOException("journal wrapped"));

        var fault = await WatchReads.ThrowsNextAsync<DriveWatchFaultException>(readerD);
        Assert.AreEqual('D', fault.DriveLetter);
        Assert.AreEqual("journal wrapped", fault.Message);
        runC.Push(1, "c.txt", 110);
        var batch = await WatchReads.NextBatchAsync(readerC);
        Assert.AreEqual(7ul, batch.JournalIdentifier);
        Assert.AreEqual(110L, batch.NextUsn);
        Assert.AreEqual("c.txt", batch.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task WatchSource_EveryDriveFaultsOnItsOwnHandle()
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
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerC));
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerD));

        var faultC = await WatchReads.ThrowsNextAsync<DriveWatchFaultException>(readerC);
        var faultD = await WatchReads.ThrowsNextAsync<DriveWatchFaultException>(readerD);

        Assert.AreEqual(('C', "C wrapped"), (faultC.DriveLetter, faultC.Message));
        Assert.AreEqual(('D', "D wrapped"), (faultD.DriveLetter, faultD.Message));
    }

    [TestMethod]
    public async Task WatchSource_StartFailsWhenItCannotConnect()
    {
        var connectFailure = new IOException("the broker never launched");
        var source = new BrokerIndexWatchSource(_ => throw connectFailure);

        var thrown = await WatchDeduplicationTestSupport.ThrowsAsync<IOException>(() =>
            source.StartAsync(new IndexWatchTarget('C', 7, 100), CancellationToken.None));

        Assert.AreSame(connectFailure, thrown);
    }

    [TestMethod]
    public async Task OneChannelLost_OnlyThatDriveFaults()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerMftBlockProducer(broker.ConnectAsync).CreateIndexSource().WatchSource!;
        using var harness = new WatchHarness(source, 'T', 'U');
        var index = harness.Index;
        await index.StartWatchingAsync('T', token);
        await index.StartWatchingAsync('U', token);
        var runT = await broker.Watch('T').RunAsync(1);
        var runU = await broker.Watch('U').RunAsync(1);
        await index.WaitForCatchUpAsync('U', token).WaitAsync(HostChannelHarness.HangGuard);

        // The host closes T's pipe: its watch ends without a word.
        runT.End();
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        var appliedOnU = ChangeSignal.WhenApplied(index, "u.txt");
        runU.Push(10, "u.txt", 300);
        await appliedOnU;

        Assert.IsInstanceOfType<BrokerChannelLostException>(fault.Exception);
        Assert.AreEqual('T', ((BrokerChannelLostException)fault.Exception).DriveLetter);
        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').Watch.CatchUpState);
        Assert.IsNotNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('U').Watch.CatchUpState);
        Assert.IsNull(harness.DriveFor('U').Watch.FailureMessage);
        Assert.IsFalse(broker.Process.Ended.IsCompleted, "one lost drive channel does not end the process");
    }

    [TestMethod]
    public async Task HostError_IsDriveWatchFault()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerMftBlockProducer(broker.ConnectAsync).CreateIndexSource().WatchSource!;
        using var harness = new WatchHarness(source, 'T', 'U');
        var index = harness.Index;
        await index.StartWatchingAsync('T', token);
        await index.StartWatchingAsync('U', token);
        var runU = await broker.Watch('U').RunAsync(1);

        (await broker.Watch('T').RunAsync(1)).Fail(new IOException("journal wrapped"));

        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.IsInstanceOfType<DriveWatchFaultException>(fault.Exception);
        Assert.AreEqual('T', ((DriveWatchFaultException)fault.Exception).DriveLetter);
        Assert.AreEqual("journal wrapped", fault.Exception.Message);
        var appliedOnU = ChangeSignal.WhenApplied(index, "u.txt");
        runU.Push(10, "u.txt", 300);
        await appliedOnU;
        Assert.IsTrue(harness.Faults.All(raised => raised.DriveLetter == 'T'), "only T raised a fault");
    }
}
