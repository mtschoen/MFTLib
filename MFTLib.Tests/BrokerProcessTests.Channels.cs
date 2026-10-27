using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task OpenChannel_CancelledBeforeHostConnects_HostSeesNoChannel()
    {
        var connecting = new TestGate();
        var connection = new TaskCompletionSource<Task<Stream>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStarts = 0;
        await using var broker = new InProcessBroker(
            BrokerHostFactory.Create(new()
            {
                WatchVolume = (_, _, _, cancellationToken) =>
                {
                    Interlocked.Increment(ref watchStarts);
                    return WatchUntilCancelledAsync(new TestGate(), new TaskCompletionSource(), cancellationToken);
                }
            }),
            wrapConnector: connect => async (pipeName, cancellationToken) =>
            {
                connecting.MarkEntered();
                await connecting.WaitForReleaseAsync(cancellationToken);
                var connected = connect(pipeName, cancellationToken);
                connection.TrySetResult(connected);
                return await connected;
            });
        using var cancellation = new CancellationTokenSource();

        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, BrokerFrame.StartWatch(Armed)),
            cancellation.Token);
        await connecting.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await cancellation.CancelAsync();
        await AssertCancelledAsync(open);
        connecting.Release();

        var connected = await connection.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        await Assert.ThrowsExactlyAsync<IOException>(() => connected.WaitAsync(HangGuard, TestContext.CancellationToken),
            "The pipe the client released has nothing listening.");
        await broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, watchStarts, "No channel ran on the host.");
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task OpenChannel_ConnectedButErrorReply_ClientDisposesStream_HostChannelEnds()
    {
        var hostEnd = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(),
            wrapConnector: connect => async (pipeName, cancellationToken) =>
            {
                hostEnd.TrySetResult(await connect(pipeName, cancellationToken));
                throw new IOException("refused after connecting");
            });

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, BrokerFrame.StartWatch(Armed)),
                CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("refused after connecting", exception.Message);
        await using var stream = await hostEnd.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        var buffer = new byte[16];
        Assert.AreEqual(0, await stream.ReadAsync(buffer, TestContext.CancellationToken).AsTask().WaitAsync(HangGuard, TestContext.CancellationToken),
            "The client closed its end, so the host's end reads EOF.");
    }

    [TestMethod]
    public async Task OpenChannel_FailConnection_ThrowsWithHostMessage()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(), new BrokerTestHarnessOptions
        {
            FailConnection = pipeName => pipeName.StartsWith(BrokerFrame.PipeNamePrefix, StringComparison.Ordinal)
                ? new IOException("no pipe for volume C")
                : null
        });

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, BrokerFrame.StartWatch(Armed)),
                CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("no pipe for volume C", exception.Message);
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task OpenChannel_FirstRequestWriteFails_DisposesBothSides()
    {
        var clientEnd = new FailingWrites();
        var hostEnd = new TaskCompletionSource<DisposalRecordingStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(),
            wrapClientStream: (name, stream) =>
                name == InMemoryBrokerPipes.ControlPipeName ? stream : clientEnd.Wrap(stream),
            wrapConnector: RecordHostEnd(hostEnd));

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, BrokerFrame.StartWatch(Armed)),
                CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual('C', lost.Volume);
        await clientEnd.Disposed.WaitAsync(HangGuard, TestContext.CancellationToken);
        await (await hostEnd.Task.WaitAsync(HangGuard, TestContext.CancellationToken)).Disposed.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(broker.Process.Ended.IsCompleted, "A lost volume channel leaves the process running.");
    }
}
