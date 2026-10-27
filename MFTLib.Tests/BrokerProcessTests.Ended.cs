using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [TestMethod]
    [DataRow("dispose", "The broker process was disposed.", false)]
    [DataRow("eof", "The broker closed its control pipe.", false)]
    [DataRow("eof", "The broker closed its control pipe.", true)]
    [DataRow("unroutable", "The broker sent CaughtUp on the control pipe.", false)]
    [DataRow("truncated", "The broker control pipe failed: Truncated broker frame on pipe", false)]
    public async Task Ended_CompletesOnceWithReason_AfterPendingRequestsFailed(
        string ending, string expectedReason, bool secondRequestGrows)
    {
        await using var broker = new ScriptedBroker();
        var beforeEnd = broker.Process.Ended;
        Assert.IsFalse(beforeEnd.IsCompleted);
        var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);
        Task second = secondRequestGrows
            ? broker.Process.GrowUsnJournalAsync('D', new UsnJournalSettings { MaximumSize = 1, AllocationDelta = 1 }, CancellationToken.None)
            : broker.Process.QueryVolumeAsync('D', CancellationToken.None);
        Assert.AreEqual(secondRequestGrows ? BrokerFrameKind.GrowUsnJournal : BrokerFrameKind.QueryVolume,
            (await broker.ReadRequestAsync()).Kind);

        if (ending == "eof")
        {
            await broker.CloseControlAsync();
        }
        else if (ending == "unroutable")
        {
            await broker.WriteControlAsync(writer => BrokerProtocol.WriteCaughtUp(writer, BrokerFrame.CaughtUp()));
        }
        else if (ending == "truncated")
        {
            await broker.WriteControlAsync(writer =>
            {
                byte[] bytes = [10, 0, 0, 0, 1, 2, 3];
                bytes.CopyTo(writer.GetSpan(bytes.Length));
                writer.Advance(bytes.Length);
            });
            await broker.CloseControlAsync();
        }
        else
        {
            await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        }

        Assert.AreEqual(expectedReason, await beforeEnd.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(0, broker.Process.Hooks.PendingRequestCount, "pending requests were failed before Ended completed");
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSame(beforeEnd, broker.Process.Ended, "the same task is returned each time");
        Assert.AreEqual(expectedReason, await broker.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken),
            "a caller that looks after the end still gets the reason");
        foreach (var request in new[] { first, second })
        {
            var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(
                () => request.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.IsNull(lost.Volume);
            Assert.AreEqual(expectedReason, lost.Message);
        }

        var rejected = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(
            () => broker.Process.QueryVolumeAsync('E', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(expectedReason, rejected.Message);
        Assert.IsNull(rejected.Volume, "the control pipe is what was lost");
    }

    [TestMethod]
    public async Task Ended_ControlPipeFailsToClose_StillCompletesWithoutFaulting()
    {
        await using var broker = new ScriptedBroker(wrapClientControl: stream => new ThrowOnAsyncDisposeStream(stream));

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

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

        Assert.AreEqual("The broker closed its control pipe.", await broker.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard, TestContext.CancellationToken));
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Ended_StallTimerFailsToDrain_StillCompletesWithoutFaulting()
    {
        await using var broker = new ScriptedBroker(new ThrowOnTimerDisposeClock());
        var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await broker.ReadRequestAsync();

        await broker.CloseControlAsync();

        Assert.AreEqual("The broker closed its control pipe.", await broker.Process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<IOException>(() => broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken));
    }
}
