using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     What a watch sees when the broker process ends: the process reports it once through
///     <see cref="BrokerProcess.Ended" />, and every drive's channel reads the host's closed pipe as
///     its own loss, naming the drive.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerDeathTests
{
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    [TestMethod]
    public async Task WatchChannel_ProcessDeath_CompletesEnded_AndReadThrowsChannelLost()
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);
        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var __ = reader.ConfigureAwait(false);
        var pending = reader.MoveNextAsync().AsTask();

        await EndHostAsync(broker, host);

        var lost = await WatchDeduplicationTestSupport.ThrowsAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        await broker.Process.Ended.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task WatchChannel_DeathBeforeRead_LateReadsThrow_EndedCompletes()
    {
        // The host ends while nothing reads: a read that starts afterwards still fails, for the
        // drive it was watching, and so does a watch that starts after the death.
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);

        await EndHostAsync(broker, host);
        await broker.Process.Ended.WaitAsync(HangGuard);

        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var __ = reader.ConfigureAwait(false);
        var late = await WatchReads.ThrowsNextAsync<BrokerChannelLostException>(reader);
        Assert.AreEqual('C', late.DriveLetter);
        var restart = await WatchDeduplicationTestSupport.ThrowsAsync<BrokerChannelLostException>(() =>
            source.StartAsync(new IndexWatchTarget('D', 7, 100), CancellationToken.None));
        Assert.AreEqual('D', restart.DriveLetter);
    }

    [TestMethod]
    public async Task ProcessDeath_FaultsEveryDriveByName_EndedCompletes()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerMftBlockProducer(broker.ConnectAsync).CreateIndexSource().WatchSource!;
        using var harness = new WatchHarness(source, 'T', 'U');
        await harness.Index.StartWatchingAsync('T', token);
        await harness.Index.StartWatchingAsync('U', token);
        await broker.Watch('T').RunAsync(1);
        await broker.Watch('U').RunAsync(1);

        await broker.EndHostAsync();

        var faultT = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        var faultU = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'U');
        await broker.Process.Ended.WaitAsync(HangGuard);
        Assert.AreEqual('T', ((BrokerChannelLostException)faultT.Exception).DriveLetter);
        Assert.AreEqual('U', ((BrokerChannelLostException)faultU.Exception).DriveLetter);
        CollectionAssert.AreEquivalent(new[] { 'T', 'U' }, harness.Faults.Select(fault => fault.DriveLetter).ToArray());
        Assert.IsTrue(harness.Faults.All(fault => fault.Kind == WatchFaultKind.Channel));
        Assert.IsNotNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.IsNotNull(harness.DriveFor('U').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUpState);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('U').WatchCatchUpState);
    }

    // The scripted host ends the way a process does: its control pipe and every drive pipe close.
    static async Task EndHostAsync(ScriptedBroker broker, Stream hostDrivePipe)
    {
        await broker.CloseControlAsync();
        await hostDrivePipe.DisposeAsync();
    }

    static async Task<(IIndexDriveWatch Handle, Stream Host)> StartAsync(BrokerIndexWatchSource source,
        ScriptedBroker broker, char drive)
    {
        var starting = source.StartAsync(new IndexWatchTarget(drive, 7, 100), CancellationToken.None);
        var host = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await HostChannelHarness.ReadFrameAsync(host))?.Kind);
        return (await starting.WaitAsync(HangGuard), host);
    }
}
