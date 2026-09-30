using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Each test waits for the limit's timer to exist on the client's clock, identified by its due
// time, then advances the clock to one tick short of the limit and to the limit itself. A fake clock
// fires timers inside Advance, so whether the timer fired is known when Advance returns.
public partial class BrokerProcessTests
{
    static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
    static readonly TimeSpan HeartbeatAfter = TimeSpan.FromSeconds(10);

    // The control pipe's stall limit is measured from its last frame, and these tests advance the
    // clock to the reply timeout, which equals it. A heartbeat after part of that time keeps the
    // pipe alive without moving the timer under test.
    static async Task KeepControlAliveAsync(ScriptedBroker broker, FakeTimeProvider clock, ReadCounter controlReads)
    {
        clock.Advance(HeartbeatAfter);
        var heartbeat = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteHeartbeat(heartbeat);
        var sent = controlReads.BytesRead + heartbeat.WrittenCount;
        await broker.WriteControlAsync(BrokerProtocol.WriteHeartbeat);
        await controlReads.WhenReadPendingAfter(sent).WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task ControlWrite_NeverFinishes_EndsProcessAfterReplyTimeout()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), BrokerLiveness.ControlReplyTimeout);
        Assert.AreEqual(BrokerLiveness.ControlReplyTimeout, BrokerProcess.ControlReplyTimeout);
        var clock = new TimerSignalingClock();
        var control = new SplitFrameWrite(splitWriteNumber: 1);
        var controlReads = new ReadCounter();
        await using var broker = new ScriptedBroker(clock, stream => controlReads.Wrap(control.Wrap(stream)));
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Process.Ended += reason => ended.TrySetResult(reason);
        // The control reader's stall limit is the first timer due after 30 seconds; the request's
        // write bound is the second.
        await clock.TimerCreated(BrokerLiveness.StallLimit).WaitAsync(HangGuard);

        var held = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await control.Gate.Entered.WaitAsync(HangGuard);
        var writeBound = await clock.TimerCreated(BrokerLiveness.ControlReplyTimeout, occurrence: 2)
            .WaitAsync(HangGuard);
        await KeepControlAliveAsync(broker, clock, controlReads);

        clock.Advance(BrokerLiveness.ControlReplyTimeout - HeartbeatAfter - OneTick);
        Assert.IsFalse(writeBound.Fired, "The write bound must not fire before the limit.");
        Assert.IsFalse(ended.Task.IsCompleted);
        clock.Advance(OneTick);

        Assert.IsTrue(writeBound.Fired);
        StringAssert.Contains(await ended.Task.WaitAsync(HangGuard), "did not finish writing within 30 seconds");
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => held.WaitAsync(HangGuard));
        Assert.IsTrue(broker.Process.HasEnded);
    }

    [TestMethod]
    public async Task OpenChannel_AcknowledgedButNeverConnected_TimesOutAndReleasesPipe()
    {
        var clock = new TimerSignalingClock();
        var controlReads = new ReadCounter();
        await using var broker = new ScriptedBroker(clock, controlReads.Wrap);
        await clock.TimerCreated(BrokerLiveness.StallLimit).WaitAsync(HangGuard);

        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
            CancellationToken.None);
        var request = await broker.ReadRequestAsync();
        // The 30 second timers so far: the control reader's stall limit, the request's write bound,
        // and its reply timeout (created after the write). The connection bound follows the reply.
        await clock.TimerCreated(BrokerLiveness.ControlReplyTimeout, occurrence: 3).WaitAsync(HangGuard);
        await broker.WriteControlAsync(writer => BrokerProtocol.WriteChannelOpened(writer, request.RequestId));
        var connectionBound = await clock.TimerCreated(BrokerLiveness.ControlReplyTimeout, occurrence: 4)
            .WaitAsync(HangGuard);
        await KeepControlAliveAsync(broker, clock, controlReads);

        clock.Advance(BrokerLiveness.ControlReplyTimeout - HeartbeatAfter - OneTick);
        Assert.IsFalse(connectionBound.Fired, "The connection bound must not fire before the limit.");
        Assert.IsFalse(open.IsCompleted);
        clock.Advance(OneTick);

        Assert.IsTrue(connectionBound.Fired);
        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => open.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "did not connect within 30 seconds");
        await Assert.ThrowsExceptionAsync<IOException>(() =>
            broker.Pipes.ConnectAsync(request.RequirePipeName(), CancellationToken.None));
        Assert.IsFalse(broker.Process.HasEnded, "Only the channel failed.");
    }
}
