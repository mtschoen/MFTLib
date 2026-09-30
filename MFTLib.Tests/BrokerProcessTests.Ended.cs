using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [DataTestMethod]
    [DataRow("dispose", "The broker process was disposed.")]
    [DataRow("eof", "The broker closed its control pipe.")]
    [DataRow("unroutable", "The broker sent CaughtUp on the control pipe.")]
    public async Task Ended_CompletesOnceWithReason_AfterPendingRequestsFailed(string ending, string expectedReason)
    {
        await using var broker = new ScriptedBroker();
        var beforeEnd = broker.Process.Ended;
        Assert.IsFalse(beforeEnd.IsCompleted);
        var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);
        var second = broker.Process.QueryVolumeAsync('D', CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);

        if (ending == "eof")
        {
            await broker.CloseControlAsync();
        }
        else if (ending == "unroutable")
        {
            await broker.WriteControlAsync(BrokerProtocol.WriteCaughtUp);
        }
        else
        {
            await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
        }

        Assert.AreEqual(expectedReason, await beforeEnd.WaitAsync(HangGuard));
        Assert.AreEqual(0, broker.Process.PendingRequestCountForTest, "pending requests were failed before Ended completed");
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.AreSame(beforeEnd, broker.Process.Ended, "the same task is returned each time");
        Assert.AreEqual(expectedReason, await broker.Process.Ended.WaitAsync(HangGuard),
            "a caller that looks after the end still gets the reason");
        foreach (var request in new Task[] { first, second })
        {
            var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(
                () => request.WaitAsync(HangGuard));
            Assert.IsNull(lost.DriveLetter);
            Assert.AreEqual(expectedReason, lost.Message);
        }

        var rejected = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(
            () => broker.Process.QueryVolumeAsync('E', CancellationToken.None).WaitAsync(HangGuard));
        Assert.AreEqual(expectedReason, rejected.Message);
    }

    [TestMethod]
    public async Task Ended_ControlPipeFailsToClose_StillCompletesWithoutFaulting()
    {
        await using var broker = new ScriptedBroker(wrapClientControl: stream => new ThrowOnAsyncDisposeStream(stream));

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.IsTrue(broker.Process.Ended.IsCompletedSuccessfully);
        Assert.AreEqual("The broker process was disposed.", await broker.Process.Ended);
    }

    [TestMethod]
    public async Task Ended_ControlPipeThrowsOnSynchronousClose_StillFailsPendingRequestsAndCompletes()
    {
        await using var broker = new ScriptedBroker(wrapClientControl: stream => new ThrowOnDisposeStream(stream));
        var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await broker.ReadRequestAsync();

        await broker.CloseControlAsync();

        Assert.AreEqual("The broker closed its control pipe.", await broker.Process.Ended.WaitAsync(HangGuard));
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task Ended_StallTimerFailsToDrain_StillCompletesWithoutFaulting()
    {
        await using var broker = new ScriptedBroker(new ThrowOnTimerDisposeClock());
        var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await broker.ReadRequestAsync();

        await broker.CloseControlAsync();

        Assert.AreEqual("The broker closed its control pipe.", await broker.Process.Ended.WaitAsync(HangGuard));
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
        await Assert.ThrowsExceptionAsync<IOException>(() => broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard));
    }
}
