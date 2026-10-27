using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     What a watch sees when the broker process ends: the process reports it once through
///     <see cref="BrokerProcess.Ended" />, and every volume's channel reads the host's closed pipe as
///     its own loss, naming the volume.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerDeathTests
{
    [TestMethod]
    public async Task WatchChannel_ProcessDeath_CompletesEnded_AndReadThrowsChannelLost()
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);
        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.CancellationToken);
        await using var __ = reader.ConfigureAwait(false);
        var pending = reader.MoveNextAsync().AsTask();

        await EndHostAsync(broker, host);

        var lost = await Assert.ThrowsAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Volume);
        await broker.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WatchChannel_DeathBeforeRead_LateReadsThrow_EndedCompletes()
    {
        // The host ends while nothing reads: a read that starts afterwards still fails, for the
        // volume it was watching, and so does a watch that starts after the death.
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);

        await EndHostAsync(broker, host);
        await broker.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);

        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.CancellationToken);
        await using var __ = reader.ConfigureAwait(false);
        var late = await WatchReads.ThrowsNextAsync<BrokerChannelLostException>(reader);
        Assert.AreEqual('C', late.Volume);
        var restart = await Assert.ThrowsAsync<BrokerChannelLostException>(() =>
            source.StartAsync(new IndexWatchTarget('D', 7, 100), CancellationToken.None));
        Assert.AreEqual('D', restart.Volume);
    }

    [TestMethod]
    public async Task ProcessDeath_FaultsEveryVolumeByName_EndedCompletes()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerMftBlockProducer(broker.ConnectAsync).CreateIndexSource().WatchSource!;
        using var harness = new WatchHarness(source, 'T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(token);
        await harness.IndexFor('U').StartWatchingAsync(token);
        await broker.Watch('T').RunAsync(1);
        await broker.Watch('U').RunAsync(1);

        await broker.EndHostAsync();

        var faultT = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        var faultU = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'U');
        await broker.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual('T', ((BrokerChannelLostException)faultT.Exception).Volume);
        Assert.AreEqual('U', ((BrokerChannelLostException)faultU.Exception).Volume);
        Assert.AreSequenceEqual(['T', 'U'], [.. harness.Faults.Select(fault => fault.Volume)], SequenceOrder.InAnyOrder);
        Assert.IsTrue(harness.Faults.All(fault => fault.Kind == WatchFaultKind.Channel));
        Assert.IsNotNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.IsNotNull(harness.RootFor('U').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('U').Watch.State);
    }

    // The scripted host ends the way a process does: its control pipe and every volume pipe close.
    static async Task EndHostAsync(ScriptedBroker broker, Stream hostVolumePipe)
    {
        await broker.CloseControlAsync();
        await hostVolumePipe.DisposeAsync();
    }

    static async Task<(IIndexVolumeWatch Handle, Stream Host)> StartAsync(BrokerIndexWatchSource source,
        ScriptedBroker broker, char drive)
    {
        var starting = source.StartAsync(new IndexWatchTarget(drive, 7, 100), CancellationToken.None);
        var host = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await HostChannelHarness.ReadFrameAsync(host))?.Kind);
        return (await starting.WaitAsync(HangGuard), host);
    }

    public TestContext TestContext { get; set; } = null!;
}
