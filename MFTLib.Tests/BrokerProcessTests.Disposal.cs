using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task Dispose_DuringScan_FailsScanWithChannelLostAndReleasesSection()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(
            scanDrive: (_, _, _, _, cancellationToken) => WaitForCancellation(scanning, sourceCancelled, cancellationToken)));

        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await scanning.Entered.WaitAsync(HangGuard);
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        await sourceCancelled.Task.WaitAsync(HangGuard);
        AssertSectionReleased(broker.Sections.Single());
    }

    // The host abandons a channel whose operation ignores cancellation once its grace period ends,
    // and its exit then closes the host's end of the scan's pipe, so the pending read ends at EOF.
    // Dispose_DuringScanWithHostEndHeldOpen_... below is the case only the channel's disposal ends.
    [TestMethod]
    public async Task Dispose_DuringScanTheHostAbandons_FailsScanWithChannelLostAndReleasesSection()
    {
        var scanning = new TestGate();
        var hostClock = new TimerSignalingClock();
        await using var broker = new InProcessBroker(CreateHost(timeProvider: hostClock,
            scanDrive: (_, _, _, _, _) =>
            {
                scanning.MarkEntered();
                scanning.WaitForRelease();
                return [[Record(5, ".", 3)]];
            }));
        try
        {
            var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
                CancellationToken.None);
            await scanning.Entered.WaitAsync(HangGuard);
            var dispose = broker.Process.DisposeAsync().AsTask();
            // The first host timer with this due time is the heartbeat sender's, created when the
            // session started; the second is the grace period the session's end starts.
            await hostClock.TimerCreated(JournalBrokerHost.ControlClosedGracePeriod, occurrence: 2).WaitAsync(HangGuard);
            hostClock.Advance(JournalBrokerHost.ControlClosedGracePeriod);
            await dispose.WaitAsync(HangGuard);

            var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
            Assert.AreEqual('C', lost.DriveLetter);
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
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new ScriptedBroker(wrapClientControl: stream => new ThrowOnAsyncDisposeStream(stream));
        broker.Process.Ended += reason => ended.TrySetResult(reason);
        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
            CancellationToken.None);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        await open.WaitAsync(HangGuard);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.IsTrue(ended.Task.IsCompleted, "The control reader was joined.");
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(hostEnd), "The drive channel was closed first.");
    }

    // A closed in-memory pipe end fails reads and writes the way a closed named pipe does, so a
    // consumer on the harness sees the failure production would, not a stream-specific one.
    [TestMethod]
    public async Task HarnessPipe_AfterClose_ReadAndWriteThrowObjectDisposed()
    {
        var pipes = new MFTLibTestExtensions.InMemoryBrokerPipes(new MFTLibTestExtensions.BrokerTestHarnessOptions(), null);
        var (client, host) = pipes.CreatePair("pipe");
        await using var hostEnd = host;
        await client.DisposeAsync();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => client.ReadAsync(new byte[4]).AsTask());
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => client.WriteAsync(new byte[4]).AsTask());
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
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        var cursor = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(cursor, Armed);
        await hostEnd.WriteAsync(cursor.WrittenMemory).AsTask().WaitAsync(HangGuard);
        await hostEnd.FlushAsync().WaitAsync(HangGuard);
        await clientReads.WhenReadPendingAfter(cursor.WrittenCount).WaitAsync(HangGuard);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        AssertSectionReleased(broker.Sections.Single());
    }

    // The host has written the scan's terminal frame and the client has read it, but the host's end
    // stays open: disposal while the client waits for EOF does not take the finished scan away.
    [TestMethod]
    public async Task Dispose_AfterTerminalFrameRead_ScanReturnsItsResult()
    {
        var clientReads = new ReadCounter();
        await using var broker = new ScriptedBroker(wrapClientDrivePipe: (_, stream) => clientReads.Wrap(stream));
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        var frames = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(frames, Armed);
        BrokerProtocol.WriteScanReady(frames, 1, 0, 0);
        BrokerProtocol.WriteJournalBatch(frames, new UsnJournalCursor(7, 1500), []);
        await hostEnd.WriteAsync(frames.WrittenMemory).AsTask().WaitAsync(HangGuard);
        await hostEnd.FlushAsync().WaitAsync(HangGuard);
        await clientReads.WhenRead(frames.WrittenCount).WaitAsync(HangGuard);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        var result = await scan.WaitAsync(HangGuard);
        using var block = result.Block.Block;
        Assert.AreEqual(new UsnJournalCursor(7, 1500), result.AdvancedCursor);
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
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using var hostEnd = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(hostEnd))?.Kind);
        var frames = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(frames, Armed);
        BrokerProtocol.WriteScanReady(frames, 1, 0, 0);
        BrokerProtocol.WriteJournalBatch(frames, new UsnJournalCursor(7, 1500), []);
        byte[] partialLengthPrefix = [10, 0];
        frames.Write(partialLengthPrefix);
        await hostEnd.WriteAsync(frames.WrittenMemory).AsTask().WaitAsync(HangGuard);
        await hostEnd.FlushAsync().WaitAsync(HangGuard);
        await clientReads.WhenReadPendingAfter(frames.WrittenCount).WaitAsync(HangGuard);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task Dispose_WithTwoPendingControlRequests_FailsBothWithChannelLost()
    {
        await using var broker = new ScriptedBroker();
        var active = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);
        var queued = broker.Process.QueryVolumeAsync('D', CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        foreach (var request in new Task[] { active, queued })
        {
            var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => request.WaitAsync(HangGuard));
            Assert.IsNull(lost.DriveLetter);
        }

        Assert.IsTrue(broker.Process.HasEnded);
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe()
    {
        var tracking = new TestGate();
        var hostEnd = new TaskCompletionSource<DisposalRecordingStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(), wrapConnector: RecordHostEnd(hostEnd));
        broker.Process.BeforeChannelTrackedForTest = () =>
        {
            tracking.MarkEntered();
            tracking.WaitForRelease();
        };

        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
            CancellationToken.None);
        await tracking.Entered.WaitAsync(HangGuard);
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
        tracking.Release();

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => open.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        await (await hostEnd.Task.WaitAsync(HangGuard)).Disposed.WaitAsync(HangGuard);
    }
}
