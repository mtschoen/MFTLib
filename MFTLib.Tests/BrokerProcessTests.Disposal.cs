using System.Buffers;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task Dispose_DuringScan_FailsScanWithChannelLostAndReleasesSection()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, cancellationToken) => WaitForCancellation(scanning, sourceCancelled, cancellationToken)
        }));

        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await scanning.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        await sourceCancelled.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        var section = broker.Sections.Single();
        AssertSectionReleased(section);
        Assert.AreEqual(1, section.Lifetime.DisposeCount, "a lifetime left over from an in-flight scan is disposed exactly once");
    }

    // The host abandons a channel whose operation ignores cancellation once its grace period ends,
    // and its exit then closes the host's end of the scan's pipe, so the pending read ends at EOF.
    // Dispose_DuringScanWithHostEndHeldOpen_... below is the case only the channel's disposal ends.
    [TestMethod]
    public async Task Dispose_DuringScanTheHostAbandons_FailsScanWithChannelLostAndReleasesSection()
    {
        var scanning = new TestGate();
        var hostClock = new TimerSignalingClock();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            TimeProvider = hostClock,
            ScanDrive = (_, _, _, _, _, _) =>
            {
                scanning.MarkEntered();
                scanning.WaitForRelease();
                return [[BrokerHostFactory.Record(5, ".", 3)]];
            }
        }));
        try
        {
            var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
                null, CancellationToken.None);
            await scanning.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            var dispose = broker.Process.DisposeAsync().AsTask();
            // The first host timer with this due time is the heartbeat sender's, created when the
            // session started; the second is the grace period the session's end starts.
            await hostClock.TimerCreated(JournalBrokerHost.ControlClosedGracePeriod, occurrence: 2).WaitAsync(HangGuard, TestContext.CancellationToken);
            hostClock.Advance(JournalBrokerHost.ControlClosedGracePeriod);
            await dispose.WaitAsync(HangGuard, TestContext.CancellationToken);

            var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.AreEqual('C', lost.Drive);
            AssertSectionReleased(broker.Sections.Single());
        }
        finally
        {
            scanning.Release();
        }
    }

    // Closing the control pipe can fail; disposal still closes every drive channel and joins the
    // control reader, and does not throw.
    [TestMethod]
    public async Task Dispose_ControlCloseThrows_CompletesTeardownWithoutThrowing()
    {
        await using var broker = new ScriptedBroker(wrapClientControl: stream => new ThrowOnAsyncDisposeStream(stream));
        var ended = broker.Process.Ended;
        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
            CancellationToken.None);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        await open.WaitAsync(HangGuard, TestContext.CancellationToken);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsTrue(ended.IsCompleted, "The control reader was joined.");
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(hostEnd), "The drive channel was closed first.");
    }

    // A closed in-memory pipe end fails reads and writes the way a closed named pipe does, so a
    // consumer on the harness sees the failure production would, not a stream-specific one.
    [TestMethod]
    public async Task HarnessPipe_AfterClose_ReadAndWriteThrowObjectDisposed()
    {
        var pipes = new InMemoryBrokerPipes(new BrokerTestHarnessOptions(), null);
        var (client, host) = pipes.CreatePair("pipe");
        await using var hostEnd = host;
        await client.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.ReadAsync(new byte[4], TestContext.CancellationToken).AsTask());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.WriteAsync(new byte[4], TestContext.CancellationToken).AsTask());
    }

    // The scripted host keeps its end of the scan's pipe open and silent, and the client is already
    // blocked in the in-memory pipe's read for the frame after Cursor when the process is disposed.
    // Closing that pipe does not complete a read it holds, so the only thing that can end it is the
    // channel's own disposal cancelling it.
    [TestMethod]
    public async Task Dispose_DuringScanWithHostEndHeldOpen_EndsPendingReadWithChannelLost()
    {
        var clientReads = new ReadCounter();
        await using var broker = new ScriptedBroker(wrapClientDrivePipe: (_, stream) => clientReads.Wrap(stream));
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        var cursor = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(cursor, Armed);
        await hostEnd.WriteAsync(cursor.WrittenMemory, TestContext.CancellationToken).AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        await hostEnd.FlushAsync(TestContext.CancellationToken).WaitAsync(HangGuard, TestContext.CancellationToken);
        await clientReads.WhenReadPendingAfter(cursor.WrittenCount).WaitAsync(HangGuard, TestContext.CancellationToken);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        AssertSectionReleased(broker.Sections.Single());
    }

    // The host has written the scan's terminal frame and the client has read it, but the host's end
    // stays open: disposal while the client waits for EOF does not take the finished scan away.
    [TestMethod]
    public async Task Dispose_AfterTerminalFrameRead_ScanReturnsItsResult()
    {
        var clientReads = new ReadCounter();
        await using var broker = new ScriptedBroker(wrapClientDrivePipe: (_, stream) => clientReads.Wrap(stream));
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        var frames = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(frames, Armed);
        BrokerProtocol.WriteScanReady(frames, 0);
        BrokerProtocol.WriteScanCompleted(frames);
        await hostEnd.WriteAsync(frames.WrittenMemory, TestContext.CancellationToken).AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        await hostEnd.FlushAsync(TestContext.CancellationToken).WaitAsync(HangGuard, TestContext.CancellationToken);
        await clientReads.WhenRead(frames.WrittenCount).WaitAsync(HangGuard, TestContext.CancellationToken);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        var result = await scan.WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block.Block;
        Assert.IsNull(result.CatchUpLoss);
        Assert.AreEqual(Armed, result.ArmedCursor);
    }

    // After the terminal frame the host starts another frame, and the client has read part of it and
    // waits for the rest when the process is disposed. The channel carried an illegal frame, so the
    // disposal cutting that read off does not leave the scan's result in place.
    [TestMethod]
    public async Task Dispose_WhileFrameAfterTerminalIsPartlyRead_FailsScanWithChannelLost()
    {
        var clientReads = new ReadCounter();
        await using var broker = new ScriptedBroker(wrapClientDrivePipe: (_, stream) => clientReads.Wrap(stream));
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        var frames = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(frames, Armed);
        BrokerProtocol.WriteScanReady(frames, 0);
        BrokerProtocol.WriteScanCompleted(frames);
        var partialLengthPrefix = BitConverter.GetBytes(10)[..2];
        frames.Write(partialLengthPrefix);
        await hostEnd.WriteAsync(frames.WrittenMemory, TestContext.CancellationToken).AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        await hostEnd.FlushAsync(TestContext.CancellationToken).WaitAsync(HangGuard, TestContext.CancellationToken);
        await clientReads.WhenReadPendingAfter(frames.WrittenCount).WaitAsync(HangGuard, TestContext.CancellationToken);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe()
    {
        var connectionGate = new TestGate();
        var controlReads = new ReadCounter();
        DisposalRecordingStream? clientPipe = null;
        await using var broker = new ScriptedBroker(
            wrapClientControl: controlReads.Wrap,
            wrapClientDrivePipe: (_, stream) => RecordClientPipe(stream, pipe => clientPipe = pipe),
            wrapPipeFactory: pipes => new ConnectedPipeGate(pipes, connectionGate));
        var firstRequestWritten = false;
        var open = broker.Process.OpenChannelAsync('C', writer =>
        {
            firstRequestWritten = true;
            BrokerProtocol.WriteStartWatch(writer, Armed);
        }, CancellationToken.None);
        try
        {
            var request = await broker.ReadRequestAsync();
            Assert.AreEqual(BrokerFrameKind.OpenChannel, request.Kind);
            await using var hostPipe = await broker.Pipes.ConnectAsync(
                request.RequirePipeName(), CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
            await connectionGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsNotNull(clientPipe);

            var reply = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteChannelOpened(reply, request.RequestId);
            await broker.WriteControlAsync(writer => BrokerProtocol.WriteChannelOpened(writer, request.RequestId));
            await controlReads.WhenReadPendingAfter(reply.WrittenCount).WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(0, broker.Process.Hooks.PendingRequestCount,
                "ChannelOpened has been routed before disposal.");
            Assert.IsFalse(open.IsCompleted);

            await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsFalse(clientPipe.Disposed.IsCompleted,
                "The untracked listener is still owned by the pending open, not the disposal sweep.");
            connectionGate.Release();

            var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => open.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.AreEqual('C', lost.Drive);
            Assert.Contains("the broker process was disposed", lost.Message);
            Assert.IsFalse(firstRequestWritten, "Tracking rejects the channel before its first request is built.");
            await clientPipe.Disposed.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsNull(await HostChannelHarness.ReadFrameAsync(hostPipe).WaitAsync(HangGuard, TestContext.CancellationToken),
                "The client disposed its connected pipe while the scripted host kept its end open.");
        }
        finally
        {
            connectionGate.Release();
            try
            {
                var opened = await open.WaitAsync(HangGuard, TestContext.CancellationToken);
                await opened.DisposeAsync();
            }
            catch (BrokerChannelLostException)
            {
                // Observe the expected failed open before the fixture releases its resources.
            }
        }
    }

    static DisposalRecordingStream RecordClientPipe(Stream stream, Action<DisposalRecordingStream> record)
    {
        var recording = new DisposalRecordingStream(stream);
        record(recording);
        return recording;
    }
}
