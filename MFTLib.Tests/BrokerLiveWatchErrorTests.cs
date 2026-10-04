using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     What a drive's watch handle does with the frames its pipe carries: an <c>Error</c> frame
///     faults only that drive's handle, and a frame a watch does not carry, a stall report or a
///     closed pipe loses only that drive's channel.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerLiveWatchErrorTests
{
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    [TestMethod]
    public async Task LiveWatch_ErrorFrameForDrive_FaultsThatDrivesHandle()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        await using var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var reader = handle.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = reader.ConfigureAwait(false);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));

        (await harness.Watch('C').RunAsync(1)).Fail(new IOException("journal wrapped"));

        var fault = await WatchReads.ThrowsNextAsync<DriveWatchFaultException>(reader);
        Assert.AreEqual("journal wrapped", fault.Message);
        Assert.AreEqual('C', fault.DriveLetter);
    }

    [TestMethod]
    public async Task LiveWatch_ErrorFrameBeforeRead_LateReaderGetsFault()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        await using var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var run = await harness.Watch('C').RunAsync(1);

        // The host's watch has failed and returned before anything reads the handle.
        run.Fail(new IOException("journal wrapped"));
        await run.Finished.WaitAsync(HangGuard);

        var reader = handle.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = reader.ConfigureAwait(false);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
        var fault = await WatchReads.ThrowsNextAsync<DriveWatchFaultException>(reader);
        Assert.AreEqual("journal wrapped", fault.Message);
    }

    [TestMethod]
    public async Task LiveWatch_FrameOutsideAWatch_LosesThatDrivesChannelAndLeavesTheOthers()
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handleC, hostC) = await StartAsync(source, broker, 'C');
        await using var _ = handleC.ConfigureAwait(false);
        await using var __ = hostC.ConfigureAwait(false);
        var (handleD, hostD) = await StartAsync(source, broker, 'D');
        await using var ___ = handleD.ConfigureAwait(false);
        await using var ____ = hostD.ConfigureAwait(false);
        var readerC = handleC.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var _____ = readerC.ConfigureAwait(false);
        var readerD = handleD.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var ______ = readerD.ConfigureAwait(false);

        await HostChannelHarness.WriteFrameAsync(hostC, writer => BrokerProtocol.WriteCursor(writer, new UsnJournalCursor(7, 1)));
        await HostChannelHarness.WriteFrameAsync(hostD, BrokerProtocol.WriteCaughtUp);

        var lost = await WatchReads.ThrowsNextAsync<BrokerChannelLostException>(readerC);
        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, nameof(BrokerFrameKind.Cursor));
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerD));
    }

    [TestMethod]
    public async Task LiveWatch_StalledFrame_LosesTheChannelWithTheHostsMessage()
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);
        await using var __ = host.ConfigureAwait(false);
        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var ___ = reader.ConfigureAwait(false);

        await HostChannelHarness.WriteFrameAsync(host, writer => BrokerProtocol.WriteStalled(writer, "the volume stopped answering"));

        var lost = await WatchReads.ThrowsNextAsync<BrokerChannelLostException>(reader);
        Assert.AreEqual('C', lost.DriveLetter);
        Assert.AreEqual("the volume stopped answering", lost.Message);
    }

    [TestMethod]
    public async Task LiveWatch_HeartbeatsBetweenFrames_AreSkipped()
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);
        await using var __ = host.ConfigureAwait(false);
        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var ___ = reader.ConfigureAwait(false);

        await HostChannelHarness.WriteFrameAsync(host, BrokerProtocol.WriteHeartbeat);
        await HostChannelHarness.WriteFrameAsync(host, BrokerProtocol.WriteCaughtUp);
        await HostChannelHarness.WriteFrameAsync(host, BrokerProtocol.WriteHeartbeat);
        await HostChannelHarness.WriteFrameAsync(host, writer => BrokerProtocol.WriteJournalBatch(writer,
            new UsnJournalCursor(7, 110), [JournalEntryFactory.Create(1, 105, "c.txt")]));

        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
        Assert.AreEqual(110L, (await WatchReads.NextBatchAsync(reader)).NextUsn);
    }

    [TestMethod]
    public async Task LiveWatch_TruncatedFrame_LosesThatDrivesChannelWithTheTruncationMessage()
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var (handle, host) = await StartAsync(source, broker, 'C');
        await using var _ = handle.ConfigureAwait(false);
        await using var __ = host.ConfigureAwait(false);
        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var ___ = reader.ConfigureAwait(false);
        await HostChannelHarness.WriteFrameAsync(host, BrokerProtocol.WriteCaughtUp);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));

        // A length prefix claiming ten bytes, three body bytes, then the pipe closes: the host died mid-frame.
        await host.WriteAsync(new byte[] { 10, 0, 0, 0, 1, 2, 3 }).AsTask().WaitAsync(HangGuard);
        await host.FlushAsync().WaitAsync(HangGuard);
        await host.DisposeAsync();

        var lost = await WatchReads.ThrowsNextAsync<BrokerChannelLostException>(reader);
        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "Truncated broker frame");
        Assert.IsFalse(process.Ended.IsCompleted, "a broken drive pipe loses that drive's channel, not the process");
    }

    // Starts a watch on the scripted broker and returns its handle with the host's end of its pipe,
    // once the client has written StartWatch.
    static async Task<(IIndexDriveWatch Handle, Stream Host)> StartAsync(BrokerIndexWatchSource source,
        ScriptedBroker broker, char drive)
    {
        var starting = source.StartAsync(new IndexWatchTarget(drive, 7, 100), CancellationToken.None);
        var host = await broker.AcceptChannelAsync();
        var request = await HostChannelHarness.ReadFrameAsync(host);
        Assert.AreEqual(BrokerFrameKind.StartWatch, request?.Kind);
        Assert.AreEqual(new UsnJournalCursor(7, 100), request?.Cursor);
        return (await starting.WaitAsync(HangGuard), host);
    }
}
