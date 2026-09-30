using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The control pipe's writer queue, its Stalled frame, and a channel whose connection never arrives
// before the process ends.
public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task ControlWrite_CallerCancelsWhileWaitingForTheWriteLock_SendsNothingAndReleasesItsId()
    {
        var control = new SplitFrameWrite(splitWriteNumber: 1);
        await using var broker = new ScriptedBroker(wrapClientControl: control.Wrap);
        var holding = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await control.Gate.Entered.WaitAsync(HangGuard);
        using var cancellation = new CancellationTokenSource();
        var waiting = broker.Process.QueryVolumeAsync('D', cancellation.Token);
        Assert.AreEqual(2, broker.Process.PendingRequestCountForTest);

        await cancellation.CancelAsync();

        await AssertCancelledAsync(waiting);
        Assert.AreEqual(1, broker.Process.PendingRequestCountForTest,
            "A request that never started writing keeps no id: nothing was sent, so no reply can come.");
        control.Gate.Release();
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);
        await broker.CloseControlAsync();
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => holding.WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task ControlWrite_CallerCancelsAfterWinningTheWriteLock_SendsNothingAndReleasesItsId()
    {
        await using var broker = new ScriptedBroker();
        using var cancellation = new CancellationTokenSource();
        broker.Process.AfterControlWriteLockAcquiredForTest = cancellation.Cancel;

        var cancelled = broker.Process.QueryVolumeAsync('C', cancellation.Token);

        await AssertCancelledAsync(cancelled);
        Assert.AreEqual(0, broker.Process.PendingRequestCountForTest,
            "Cancellation before the frame starts writing sends nothing, so the id is released.");
        broker.Process.AfterControlWriteLockAcquiredForTest = null;
        var next = broker.Process.QueryVolumeAsync('D', CancellationToken.None);
        var request = await broker.ReadRequestAsync();
        Assert.AreEqual(BrokerFrameKind.QueryVolume, request.Kind, "The first frame on the pipe is the next request's.");
        await broker.CloseControlAsync().AsTask().WaitAsync(HangGuard);
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => next.WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task ControlPipe_StalledFrame_EndsProcessWithHostMessage()
    {
        await using var broker = new ScriptedBroker();
        var ended = broker.Process.Ended;
        var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await broker.ReadRequestAsync();

        await broker.WriteControlAsync(writer => BrokerProtocol.WriteStalled(writer, "the broker stopped answering"));

        Assert.AreEqual("the broker stopped answering", await ended.WaitAsync(HangGuard));
        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
        Assert.AreEqual("the broker stopped answering", lost.Message);
        await broker.Process.Ended.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task ControlPipe_KnownReplyOfTheWrongKind_EndsProcessAndFailsEveryRequest()
    {
        await using var broker = new ScriptedBroker();
        var ended = broker.Process.Ended;
        var answered = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        var request = await broker.ReadRequestAsync();
        var pending = broker.Process.QueryVolumeAsync('D', CancellationToken.None);
        await broker.ReadRequestAsync();

        await broker.WriteControlAsync(writer =>
            BrokerProtocol.WriteUsnJournalSettings(writer, request.RequestId, 65536, 4096));

        var wrongReply = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            answered.WaitAsync(HangGuard));
        Assert.IsNull(wrongReply.DriveLetter);
        StringAssert.Contains(wrongReply.Message, "UsnJournalSettings instead of VolumeInfo");
        Assert.AreEqual(wrongReply.Message, await ended.WaitAsync(HangGuard),
            "The process ends with the mismatch as its reason.");
        await broker.Process.Ended.WaitAsync(HangGuard);
        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
        Assert.AreEqual(wrongReply.Message, lost.Message);
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            broker.Process.QueryVolumeAsync('E', CancellationToken.None).WaitAsync(HangGuard));
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task OpenChannel_AcknowledgedThenProcessEnds_FailsWithChannelLostNamingTheProcess()
    {
        await using var broker = new ScriptedBroker();
        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
            CancellationToken.None);
        var request = await broker.ReadRequestAsync();
        Assert.AreEqual(BrokerFrameKind.OpenChannel, request.Kind);

        // The host acknowledged and never connected; then the process ends while the client waits.
        await broker.WriteControlAsync(writer => BrokerProtocol.WriteChannelOpened(writer, request.RequestId));
        await broker.CloseControlAsync();

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => open.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "the broker process ended");
    }
}
