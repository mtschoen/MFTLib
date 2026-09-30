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
            CreateHost(watchDrive: (_, _, _, cancellationToken) =>
            {
                Interlocked.Increment(ref watchStarts);
                return WatchUntilCancelledAsync(new TestGate(), new TaskCompletionSource(), cancellationToken);
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

        var open = broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
            cancellation.Token);
        await connecting.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();
        await AssertCancelledAsync(open);
        connecting.Release();

        var connected = await connection.Task.WaitAsync(HangGuard);
        await Assert.ThrowsExceptionAsync<IOException>(() => connected.WaitAsync(HangGuard),
            "The pipe the client released has nothing listening.");
        await broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);
        Assert.AreEqual(0, watchStarts, "No channel ran on the host.");
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task OpenChannel_ConnectedButErrorReply_ClientDisposesStream_HostChannelEnds()
    {
        var hostEnd = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(),
            wrapConnector: connect => async (pipeName, cancellationToken) =>
            {
                hostEnd.TrySetResult(await connect(pipeName, cancellationToken));
                throw new IOException("refused after connecting");
            });

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
                CancellationToken.None).WaitAsync(HangGuard));

        StringAssert.Contains(exception.Message, "refused after connecting");
        await using var stream = await hostEnd.Task.WaitAsync(HangGuard);
        var buffer = new byte[16];
        Assert.AreEqual(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(HangGuard),
            "The client closed its end, so the host's end reads EOF.");
    }

    [TestMethod]
    public async Task OpenChannel_FailConnection_ThrowsWithHostMessage()
    {
        await using var broker = new InProcessBroker(CreateHost(), new BrokerTestHarnessOptions
        {
            FailConnection = pipeName => pipeName.Contains("-C-", StringComparison.Ordinal)
                ? new IOException("no pipe for drive C")
                : null
        });

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
                CancellationToken.None).WaitAsync(HangGuard));

        StringAssert.Contains(exception.Message, "no pipe for drive C");
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task OpenChannel_FirstRequestWriteFails_DisposesBothSides()
    {
        var clientEnd = new FailingWrites();
        var hostEnd = new TaskCompletionSource<DisposalRecordingStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(),
            wrapClientStream: (name, stream) =>
                name == InMemoryBrokerPipes.ControlPipeName ? stream : clientEnd.Wrap(stream),
            wrapConnector: RecordHostEnd(hostEnd));

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            broker.Process.OpenChannelAsync('C', writer => BrokerProtocol.WriteStartWatch(writer, Armed),
                CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual('C', lost.DriveLetter);
        await clientEnd.Disposed.WaitAsync(HangGuard);
        await (await hostEnd.Task.WaitAsync(HangGuard)).Disposed.WaitAsync(HangGuard);
        Assert.IsFalse(broker.Process.Ended.IsCompleted, "A lost drive channel leaves the process running.");
    }
}
