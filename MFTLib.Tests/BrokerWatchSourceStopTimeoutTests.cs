using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BrokerWatchSourceStopTimeoutTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnacknowledgedStop_RefusesReuseBeforeAndAfterLateAcknowledgement(bool disposeIndex)
    {
        using var timeout = new ZeroAcknowledgementTimeout();
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerIndexWatchSource(broker.ConnectAsync);
        using var first = new WatchHarness(source);

        await first.Index.StartWatchingAsync(token).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await broker.ReadFrameAsync()).Kind);
        if (disposeIndex)
        {
            await first.Index.DisposeAsync().AsTask().WaitAsync(token);
        }
        else
        {
            await first.Index.StopWatchingAsync(token).WaitAsync(token);
        }

        Assert.AreEqual(BrokerFrameKind.EndWatch, (await broker.ReadFrameAsync()).Kind);
        Assert.IsTrue(broker.Client.LastStopTimedOut);

        // A disposed index cannot restart, so that row uses another index with the same source.
        using var next = disposeIndex ? new WatchHarness(source) : null;
        var index = next?.Index ?? first.Index;
        await AssertRefusedAsync(index, token);
        Assert.AreEqual(1, broker.ConnectionCount);

        await broker.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await AssertRefusedAsync(index, token);
        Assert.AreEqual(1, broker.ConnectionCount);
    }

    [TestMethod]
    public async Task AcknowledgedStop_AllowsAnotherWatchToCatchUp()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var source = new BrokerIndexWatchSource(broker.ConnectAsync);
        using var harness = new WatchHarness(source);

        await harness.Index.StartWatchingAsync(token).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await broker.ReadFrameAsync()).Kind);
        await StopWithAcknowledgementAsync(harness.Index, broker);
        Assert.IsFalse(broker.Client.LastStopTimedOut);

        await harness.Index.StartWatchingAsync(token).WaitAsync(token);
        var start = await broker.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        Assert.AreEqual(2, broker.ConnectionCount);
        await broker.WriteAsync(writer => BrokerProtocol.WriteCaughtUp(
            writer, "T:", broker.ArmEpochForDrive(start, 'T')));
        await harness.Index.WaitForCatchUpAsync('T', token).WaitAsync(token);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.Index.Drives[0].WatchCatchUp);
        await StopWithAcknowledgementAsync(harness.Index, broker);
        Assert.IsFalse(broker.Client.LastStopTimedOut);
    }

    static async Task AssertRefusedAsync(FileIndex index, CancellationToken token)
    {
        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StartWatchingAsync(token).WaitAsync(token));
        Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);
        StringAssert.Contains(failure.InnerException!.Message, "EndWatchAck");
        StringAssert.Contains(failure.Message, "Create a new connection and watch source.");
    }

    static async Task StopWithAcknowledgementAsync(FileIndex index, ScriptedWatchBrokerHarness broker)
    {
        var stop = index.StopWatchingAsync(broker.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await broker.ReadFrameAsync()).Kind);
        await broker.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await stop.WaitAsync(broker.CancellationToken);
    }

    sealed class ZeroAcknowledgementTimeout : IDisposable
    {
        readonly TimeSpan _previous = JournalBrokerClient._endWatchAckTimeout;

        public ZeroAcknowledgementTimeout()
        {
            JournalBrokerClient._endWatchAckTimeout = TimeSpan.Zero;
        }

        public void Dispose()
        {
            JournalBrokerClient._endWatchAckTimeout = _previous;
        }
    }
}
