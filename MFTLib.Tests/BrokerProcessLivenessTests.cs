// Over 500 lines because the scripted-session helpers and timer fakes serve every liveness test in one place.
using System.Buffers;
using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Every limit is measured on a fake clock. A test that must know the client is waiting on a pipe
// before it advances the clock waits for that read to be pending (ReadCounter), and a frame the
// client has to have consumed is proven by the next read being pending after its bytes. The stall
// limit measures from the start of the current read, so a test that keeps a pipe alive advances the
// clock by less than the limit between frames.
[TestClass]
public class BrokerProcessLivenessTests
{
    const string StallMessage = "No frame from the broker for 30 seconds";
    static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
    static readonly TimeSpan StallLimit = BrokerLiveness.StallLimit;
    static readonly TimeSpan HalfWindow = TimeSpan.FromSeconds(10);
    static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(20);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);

    [TestMethod]
    public async Task ControlSilentPastStallLimit_EndsProcess()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), BrokerLiveness.StallLimit);
        var clock = new TimerSignalingClock();
        await using var session = new LivenessSession(clock);
        var ended = session.Process.Ended;
        await session.ControlReads.WhenReadPendingAfter(0).WaitAsync(HangGuard, TestContext.CancellationToken);
        var stallTimer = await clock.TimerCreated(StallLimit).WaitAsync(HangGuard, TestContext.CancellationToken);

        clock.Advance(StallLimit - OneTick);
        Assert.IsFalse(stallTimer.Fired, "The stall limit must not fire before the limit.");
        Assert.IsFalse(ended.IsCompleted);
        Assert.IsFalse(session.Process.Ended.IsCompleted);
        clock.Advance(OneTick);

        Assert.IsTrue(stallTimer.Fired);
        Assert.AreEqual(StallMessage, await ended.WaitAsync(HangGuard, TestContext.CancellationToken));
        await session.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            session.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.IsNull(lost.Drive);
        Assert.AreEqual(StallMessage, lost.Message);
    }

    [TestMethod]
    public async Task ControlHeartbeats_KeepProcessAlive()
    {
        var clock = new FakeTimeProvider();
        await using var session = new LivenessSession(clock);
        await session.ControlReads.WhenReadPendingAfter(0).WaitAsync(HangGuard, TestContext.CancellationToken);

        for (var heartbeat = 0; heartbeat < 4; heartbeat++)
        {
            clock.Advance(SendInterval);
            await session.SendControlAsync(BrokerProtocol.WriteHeartbeat);
            await session.ControlSettledAsync();
        }

        Assert.IsFalse(session.Process.Ended.IsCompleted,
            "Four intervals of 20 seconds pass the 30 second limit; heartbeats reset it.");
        var query = session.Process.QueryVolumeAsync('C', CancellationToken.None);
        await session.AnswerQueryVolumeAsync(Volume);
        Assert.AreEqual(Volume.MftValidDataLength, (await query.WaitAsync(HangGuard, TestContext.CancellationToken)).MftValidDataLength);
    }

    [TestMethod]
    public async Task ScanChannelSilent_FailsScanWithChannelLost_OtherScanUnaffected()
    {
        var clock = new FakeTimeProvider();
        await using var session = new LivenessSession(clock);
        var silent = await session.StartScanAsync('C');
        var healthy = await session.StartScanAsync('D');
        await session.ControlSettledAsync();

        clock.Advance(SendInterval);
        await healthy.SendAsync(BrokerProtocol.WriteHeartbeat);
        await session.SendControlAsync(BrokerProtocol.WriteHeartbeat);
        await healthy.SettledAsync();
        await session.ControlSettledAsync();
        clock.Advance(HalfWindow - OneTick);
        Assert.IsFalse(silent.Scan.IsCompleted, "The channel has been silent for one tick less than the limit.");
        clock.Advance(OneTick);

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            silent.Scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        Assert.AreEqual(StallMessage, lost.Message);
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(silent.HostPipe).WaitAsync(HangGuard, TestContext.CancellationToken),
            "The client closes the silent pipe, which the host reads as EOF.");
        Assert.IsFalse(session.Process.Ended.IsCompleted, "Only the silent channel failed.");
        Assert.IsFalse(healthy.Scan.IsCompleted);
        await healthy.FinishAsync();
        var result = await healthy.Scan.WaitAsync(HangGuard, TestContext.CancellationToken);
        result.Block.Block.Dispose();
        Assert.AreEqual(new DriveKey('D'), result.Drive);
    }

    [TestMethod]
    public async Task ScanProgressKeepsScanAlive()
    {
        var clock = new FakeTimeProvider();
        await using var session = new LivenessSession(clock);
        var reports = 0;
        var scan = await session.StartScanAsync('C', new SynchronousProgress<BlockWriteProgress>(_ => reports++));
        await session.ControlSettledAsync();

        for (var report = 0; report < 4; report++)
        {
            clock.Advance(SendInterval);
            var sample = new BrokerScanProgress
            {
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = report,
                BytesProcessed = report,
                TotalRecords = 100,
                TotalBytes = 100
            };
            await scan.SendAsync(writer => BrokerProtocol.WriteScanProgress(writer, sample));
            await session.SendControlAsync(BrokerProtocol.WriteHeartbeat);
            await scan.SettledAsync();
            await session.ControlSettledAsync();
        }

        Assert.AreEqual(4, reports);
        Assert.IsFalse(scan.Scan.IsCompleted, "The scan is running, not failed.");
        await scan.FinishAsync();
        (await scan.Scan.WaitAsync(HangGuard, TestContext.CancellationToken)).Block.Block.Dispose();
    }

    [TestMethod]
    public async Task ScanChannelSilentAfterTerminalFrame_FailsAsLostChannel()
    {
        var clock = new FakeTimeProvider();
        await using var session = new LivenessSession(clock);
        var scan = await session.StartScanAsync('C');
        await scan.SendAsync(writer => BrokerProtocol.WriteScanReady(writer, 0));
        await scan.SendAsync(BrokerProtocol.WriteScanCompleted);
        await scan.SettledAsync();
        await session.ControlSettledAsync();

        clock.Advance(SendInterval);
        await session.SendControlAsync(BrokerProtocol.WriteHeartbeat);
        await session.ControlSettledAsync();
        clock.Advance(HalfWindow);

        // The host never closed the channel after its terminal frame. The stall is the peer's
        // silence, not this scan closing its own channel, so the complete result is not returned.
        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.Scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        Assert.AreEqual(StallMessage, lost.Message);
    }

    [TestMethod]
    public async Task ScanChannelStalledFrame_FailsWithHostMessage()
    {
        var clock = new FakeTimeProvider();
        await using var session = new LivenessSession(clock);
        var stalled = await session.StartScanAsync('C');
        var other = await session.StartScanAsync('D');
        const string hostMessage = "Drive C stalled in step Parsing MFT records for 30 seconds";

        await stalled.SendAsync(writer => BrokerProtocol.WriteStalled(writer, hostMessage));

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            stalled.Scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        Assert.AreEqual(hostMessage, lost.Message);
        Assert.IsFalse(session.Process.Ended.IsCompleted);
        Assert.IsFalse(other.Scan.IsCompleted);
        await other.FinishAsync();
        (await other.Scan.WaitAsync(HangGuard, TestContext.CancellationToken)).Block.Block.Dispose();
    }

    [TestMethod]
    public async Task ControlReply_TimesOut_LateReplyDropped_NextRequestSucceeds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), BrokerLiveness.ControlReplyTimeout);
        var clock = new TimerSignalingClock();
        await using var session = new LivenessSession(clock);
        await session.ControlReads.WhenReadPendingAfter(0).WaitAsync(HangGuard, TestContext.CancellationToken);
        await clock.TimerCreated(StallLimit).WaitAsync(HangGuard, TestContext.CancellationToken);

        var slow = session.Process.QueryVolumeAsync('C', CancellationToken.None);
        var slowRequest = await session.Broker.ReadRequestAsync();
        // The 30 second timers so far are the control reader's stall limit, the request's write
        // bound (created before the write) and its reply timeout (created after it).
        var replyTimeout = await clock.TimerCreated(BrokerLiveness.ControlReplyTimeout, occurrence: 3)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        clock.Advance(HalfWindow);
        await session.SendControlAsync(BrokerProtocol.WriteHeartbeat);
        await session.ControlSettledAsync();

        clock.Advance(StallLimit - HalfWindow - OneTick);
        Assert.IsFalse(replyTimeout.Fired, "The reply timeout must not fire before the limit.");
        Assert.IsFalse(slow.IsCompleted);
        clock.Advance(OneTick);

        Assert.IsTrue(replyTimeout.Fired);
        var timeout = await Assert.ThrowsExactlyAsync<TimeoutException>(() => slow.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.Contains("did not answer request", timeout.Message);
        Assert.Contains("30 seconds", timeout.Message);
        Assert.IsFalse(session.Process.Ended.IsCompleted, "Only the request timed out.");
        Assert.AreEqual(1, session.Process.Hooks.PendingRequestCount, "The id stays until its reply arrives.");

        await session.SendControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, slowRequest.RequestId, 1, 111));
        await session.ControlSettledAsync();
        Assert.AreEqual(0, session.Process.Hooks.PendingRequestCount, "The late reply is dropped with its id.");

        var next = session.Process.QueryVolumeAsync('C', CancellationToken.None);
        var nextRequest = await session.Broker.ReadRequestAsync();
        Assert.AreNotEqual(slowRequest.RequestId, nextRequest.RequestId);
        await session.SendControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, nextRequest.RequestId,
            Volume.BytesPerFileRecordSegment, 222));
        Assert.AreEqual(222L, (await next.WaitAsync(HangGuard, TestContext.CancellationToken)).MftValidDataLength);
    }

    [TestMethod]
    public async Task ReaderSilentPipe_FailsAtStallLimitBoundary_AndClosesPipe()
    {
        var clock = new TimerSignalingClock();
        var (client, host) = new InMemoryPipePair();
        await using var hostPipe = host;
        await using var reader = new BrokerFrameReader(client, 'C', "test", clock);

        var read = reader.ReadAsync(CancellationToken.None).AsTask();
        var stallTimer = await clock.TimerCreated(StallLimit).WaitAsync(HangGuard, TestContext.CancellationToken);
        clock.Advance(StallLimit - OneTick);

        Assert.IsFalse(stallTimer.Fired, "The stall limit must not fire before the limit.");
        Assert.IsFalse(read.IsCompleted);
        clock.Advance(OneTick);

        Assert.IsTrue(stallTimer.Fired);
        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => read.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Drive);
        Assert.AreEqual(StallMessage, lost.Message);
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(hostPipe).WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ReaderFrame_RestartsStallWindowFromNextRead()
    {
        var clock = new FakeTimeProvider();
        var (client, host) = new InMemoryPipePair();
        await using var hostPipe = host;
        await using var reader = new BrokerFrameReader(client, 'C', "test", clock);
        var first = reader.ReadAsync(CancellationToken.None).AsTask();
        clock.Advance(SendInterval);
        await HostChannelHarness.WriteFrameAsync(hostPipe, BrokerProtocol.WriteHeartbeat).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.Heartbeat, (await first.WaitAsync(HangGuard, TestContext.CancellationToken))?.Kind);

        var second = reader.ReadAsync(CancellationToken.None).AsTask();
        clock.Advance(StallLimit - OneTick);
        Assert.IsFalse(second.IsCompleted, "The window restarted at the second read, 20 seconds in.");
        clock.Advance(SendInterval);

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => second.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(StallMessage, lost.Message);
    }

    [TestMethod]
    public async Task ReaderFrameLandingAfterStallClaimedTheRead_StallWinsExactlyOnce()
    {
        var clock = new FakeTimeProvider();
        var (client, host) = new InMemoryPipePair();
        await using var hostPipe = host;
        var gate = new TestGate();
        await using var reader = new BrokerFrameReader(new IgnoreCancellationStream(client, gate), 'C', "test", clock);
        await HostChannelHarness.WriteFrameAsync(hostPipe, BrokerProtocol.WriteHeartbeat).WaitAsync(HangGuard, TestContext.CancellationToken);
        var read = reader.ReadAsync(CancellationToken.None).AsTask();
        await gate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        // The stall claims the read; the stream ignores the cancellation, so its frame lands after.
        clock.Advance(StallLimit);
        gate.Release();

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => read.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(StallMessage, lost.Message);
    }

    [TestMethod]
    public async Task ReaderDispose_WaitsForARunningStallCallback()
    {
        var clock = new CallbackDrainingClock();
        var (client, hostPipe) = new InMemoryPipePair();
        var reader = new BrokerFrameReader(client, 'C', "test", clock);
        var read = reader.ReadAsync(CancellationToken.None).AsTask();
        await clock.TimerCreated.WaitAsync(HangGuard, TestContext.CancellationToken);
        var advance = Task.Run(() => clock.Advance(StallLimit), TestContext.CancellationToken);
        await clock.CallbackEntered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var dispose = reader.DisposeAsync().AsTask();
        await clock.DisposeEntered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(dispose.IsCompleted, "Disposal must wait while the stall callback is running.");
        clock.CallbackEntered.Release();
        await dispose.WaitAsync(HangGuard, TestContext.CancellationToken);
        await advance.WaitAsync(HangGuard, TestContext.CancellationToken);

        await hostPipe.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsNull(await read.WaitAsync(HangGuard, TestContext.CancellationToken), "The disposed timer's late callback stalled nothing.");
    }

    [TestMethod]
    public async Task ControlReaderEnd_DrainsStallTimerBeforeProcessEnds()
    {
        var clock = new CallbackDrainingClock();
        await using var session = new LivenessSession(clock);
        var ended = session.Process.Ended;
        await session.ControlReads.WhenReadPendingAfter(0).WaitAsync(HangGuard, TestContext.CancellationToken);
        await clock.TimerCreated.WaitAsync(HangGuard, TestContext.CancellationToken);
        var advance = Task.Run(() => clock.Advance(StallLimit - OneTick), TestContext.CancellationToken);
        await advance.WaitAsync(HangGuard, TestContext.CancellationToken);
        advance = Task.Run(() => clock.Advance(OneTick), TestContext.CancellationToken);
        await clock.CallbackEntered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await session.Broker.CloseControlAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        await clock.DisposeEntered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsFalse(ended.IsCompleted, "Ended must not be raised while the stall timer is draining.");
        Assert.IsFalse(session.Process.Ended.IsCompleted);
        clock.CallbackEntered.Release();
        await advance.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.Contains("closed its control pipe", await ended.WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    static int FrameLength(Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        return buffer.WrittenCount;
    }

    // A ScriptedBroker on a fake clock whose control pipe and drive pipes count what the client has read.
    sealed class LivenessSession : IAsyncDisposable
    {
        readonly ConcurrentDictionary<char, ReadCounter> _driveReads = new();
        long _controlSent;

        public LivenessSession(TimeProvider clock)
        {
            Broker = new ScriptedBroker(clock, ControlReads.Wrap,
                (pipeName, stream) => DriveReads(pipeName.Split('-')[^2][0]).Wrap(stream));
        }

        public ScriptedBroker Broker { get; }

        public BrokerProcess Process => Broker.Process;

        public ReadCounter ControlReads { get; } = new();

        ReadCounter DriveReads(char letter) => _driveReads.GetOrAdd(letter, _ => new ReadCounter());

        public Task SendControlAsync(Action<IBufferWriter<byte>> write)
        {
            _controlSent += FrameLength(write);
            return Broker.WriteControlAsync(write).WaitAsync(HangGuard);
        }

        // Completes once the client has read every control byte sent and waits for more.
        public Task ControlSettledAsync() => ControlReads.WhenReadPendingAfter(_controlSent).WaitAsync(HangGuard);

        public async Task AnswerQueryVolumeAsync(NtfsVolumeInformation volume)
        {
            var request = await Broker.ReadRequestAsync();
            Assert.AreEqual(BrokerFrameKind.QueryVolume, request.Kind);
            await SendControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, request.RequestId,
                volume.BytesPerFileRecordSegment, volume.MftValidDataLength));
        }

        // Starts a scan and scripts it to the point where the client waits for ScanReady.
        public async Task<StubbedScan> StartScanAsync(char letter, IProgress<BlockWriteProgress>? progress = null)
        {
            var scan = Broker.Process.ScanDriveAsync(letter, TestBlockSections.Target(),
                new MftScanOptions(), progress, CancellationToken.None);
            await AnswerQueryVolumeAsync(Volume);
            var open = await Broker.ReadRequestAsync();
            Assert.AreEqual(BrokerFrameKind.OpenChannel, open.Kind);
            var pipe = await Broker.Pipes.ConnectAsync(open.RequirePipeName(), CancellationToken.None)
                .WaitAsync(HangGuard);
            await SendControlAsync(writer => BrokerProtocol.WriteChannelOpened(writer, open.RequestId));
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(pipe).WaitAsync(HangGuard))?.Kind);
            var scripted = new StubbedScan(pipe, DriveReads(letter), scan);
            await scripted.SendAsync(writer => BrokerProtocol.WriteCursor(writer, new UsnJournalCursor(7, 1000)));
            await scripted.SettledAsync();
            return scripted;
        }

        public ValueTask DisposeAsync() => Broker.DisposeAsync();
    }

    // The host's end of one scan channel, with the count of bytes it has sent.
    sealed class StubbedScan(Stream hostPipe, ReadCounter reads, Task<BrokerDriveScanResult> scan)
    {
        long _sent;

        public Stream HostPipe => hostPipe;

        public Task<BrokerDriveScanResult> Scan => scan;

        public Task SendAsync(Action<IBufferWriter<byte>> write)
        {
            _sent += FrameLength(write);
            return HostChannelHarness.WriteFrameAsync(hostPipe, write).WaitAsync(HangGuard);
        }

        public Task SettledAsync() => reads.WhenReadPendingAfter(_sent).WaitAsync(HangGuard);

        // The rest of a scan: ScanReady, the terminal ScanCompleted, then the host closing the channel.
        public async Task FinishAsync()
        {
            await SendAsync(writer => BrokerProtocol.WriteScanReady(writer, 0));
            await SendAsync(BrokerProtocol.WriteScanCompleted);
            await hostPipe.DisposeAsync().AsTask().WaitAsync(HangGuard);
        }
    }

    // Delivers bytes only after its gate is released, and ignores the cancellation token, as a read
    // that has already returned to the reader's continuation does.
    sealed class IgnoreCancellationStream(Stream inner, TestGate gate) : DelegatingStream(inner)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            gate.MarkEntered();
            await gate.WaitForReleaseAsync(CancellationToken.None).WaitAsync(HangGuard, CancellationToken.None);
            return await Inner.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(HangGuard, CancellationToken.None);
        }
    }

    // A clock whose timers behave as a real timer does at disposal: DisposeAsync finishes only when
    // no callback is running. The first callback to run is held on CallbackEntered's gate.
    sealed class CallbackDrainingClock : FakeTimeProvider
    {
        readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestGate CallbackEntered { get; } = new();

        public TestGate DisposeEntered { get; } = new();

        public Task TimerCreated => _created.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new CallbackDrainingTimer(this, callback);
            timer.Attach(base.CreateTimer(timer.Run, state, dueTime, period));
            _created.TrySetResult();
            return timer;
        }
    }

    sealed class CallbackDrainingTimer(CallbackDrainingClock clock, TimerCallback callback) : ITimer
    {
        readonly Lock _gate = new();
        readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ITimer _inner = null!;
        int _running;

        public void Attach(ITimer inner) => _inner = inner;

        public void Run(object? state)
        {
            lock (_gate)
            {
                _running++;
            }

            try
            {
                clock.CallbackEntered.MarkEntered();
                clock.CallbackEntered.WaitForRelease();
                callback(state);
            }
            finally
            {
                lock (_gate)
                {
                    _running--;
                    if (_running == 0)
                    {
                        _idle.TrySetResult();
                    }
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => _inner.Change(dueTime, period);

        public void Dispose() => _inner.Dispose();

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().AsTask().WaitAsync(HangGuard);
            clock.DisposeEntered.MarkEntered();
            lock (_gate)
            {
                if (_running == 0)
                {
                    _idle.TrySetResult();
                }
            }

            await _idle.Task.WaitAsync(HangGuard);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
