using MFTLibTestExtensions;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The failure and teardown edges of one channel's open and of the control session around it: a
// request the host cannot serve, a reply or a connection that arrives after the client or the
// session is gone, and the reader that ends a channel when its client pipe breaks.
public partial class JournalBrokerHostChannelTests
{
    [TestMethod]
    public async Task OpenChannel_InvalidDriveLetter_RepliesErrorWithRequestId()
    {
        var host = CreateHost();
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 31, "12", "unused"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(31u, reply.RequestId);
        StringAssert.Contains(reply.Message, "'12' is not a valid drive letter");
    }

    [TestMethod]
    public async Task ControlPipe_DriveOperationFrame_RepliesErrorNamingTheKind()
    {
        var host = CreateHost();
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(BrokerProtocol.WriteCaughtUp);
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        StringAssert.Contains(reply.Message, "CaughtUp is not a control request");
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 4, "C"));
        Assert.AreEqual(4u, (await harness.ReadControlAsync()).RequestId, "The session keeps serving.");
    }

    [TestMethod]
    public async Task ControlRequest_SourceCancelsItselfWhileSessionLives_RepliesError()
    {
        var host = CreateHost(queryVolumeInfo: _ => throw new OperationCanceledException("the source gave up"));
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 6, "C"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(6u, reply.RequestId);
        Assert.AreEqual("the source gave up", reply.Message);
        Assert.IsFalse(harness.Serve.IsCompleted, "One request's own cancellation does not end the session.");
    }

    [TestMethod]
    public async Task OpenChannel_ChannelOpenedReplyCannotBeWritten_DisposesTheConnectedPipeAndEndsSession()
    {
        await using var pair = new InMemoryPipePair();
        var connected = new DisposalRecordingStream(pair.Host);
        var host = CreateHost();
        await using var harness = new HostChannelHarness(host, breakableControl: true,
            connectChannel: (_, _) => Task.FromResult<Stream>(connected));
        // The client is gone when its request arrives, so the ChannelOpened reply is the first
        // frame to land on the dead control pipe, after the host connected the drive pipe.
        harness.BreakableControl!.BreakPipe();

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 8, "C", "pipe"));

        await connected.Disposed.WaitAsync(HostChannelHarness.HangGuard);
        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task OpenChannel_ConnectionArrivesAfterSessionEnded_IsDisposed()
    {
        var connection = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = new TestGate();
        var host = CreateHost();
        await using var harness = new HostChannelHarness(host, connectChannel: (_, _) =>
        {
            connecting.MarkEntered();
            return connection.Task;
        });
        await using var late = new InMemoryPipePair();
        await using var lateStream = new DisposalRecordingStream(late.Host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 9, "C", "slow"));
        await connecting.Entered.WaitAsync(HostChannelHarness.HangGuard);
        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
        connection.SetResult(lateStream);

        await lateStream.Disposed.WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError()
    {
        var clock = new TimerSignalingClock();
        var host = CreateHost(timeProvider: clock);
        var readCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionsReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostChannelHarness(host, wrapDrivePipe: (_, hostEnd) =>
            new UncancellableReadStream(hostEnd, readCancelled, releaseRead.Task),
            connectionsReleased: connectionsReleased.Task);
        var pipe = await OpenChannelAfterConnectBoundAsync(harness, clock, connectionsReleased, 'C');

        // The host times out, cancels its read, and the read finishes by returning end of stream
        // rather than by throwing: the Error is still written and the channel ends.
        await AdvanceWhenTimerExistsAsync(clock, JournalBrokerHost.FirstRequestTimeout, 2, readCancelled.Task);
        releaseRead.SetResult();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        StringAssert.Contains(frames[0].Message, "received no request within");
    }

    [TestMethod]
    public async Task WatchChannel_ClientPipeReadFails_CancelsTheWatchAndClosesTheChannel()
    {
        using var readBreaker = new ReadBreaker();
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(queryCursor: _ => Armed,
            watchDrive: (_, _, _, token) => ParkedUntilCancelled(watchStopped, token));
        await using var harness = new HostChannelHarness(host, wrapDrivePipe: readBreaker.Wrap);
        var pipe = await harness.OpenWatchChannelAsync('C', Armed);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);

        // The client's end is gone the way a broken named pipe reports it: the read throws.
        readBreaker.BreakReads();

        await watchStopped.Task.WaitAsync(HostChannelHarness.HangGuard);
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(pipe), "The host closes the channel.");
        Assert.IsFalse(harness.Serve.IsCompleted, "One channel's broken pipe does not end the session.");
    }

    [TestMethod]
    public async Task ControlSessionEnds_ChannelTrackedWhileDrainIsWaiting_DrainWaitsForItToo()
    {
        var clock = new TimerSignalingClock();
        var host = CreateHost(timeProvider: clock);
        var (client, hostControl) = DuplexStream.CreatePair();
        await using var serverEnd = hostControl;
        await using var pipes = new InMemoryPipePair();
        var heldReply = new HeldReply();
        var connected = new GatedDisposalStream(pipes.Host);
        var serve = host.ServeAsync(heldReply.Wrap(serverEnd), (_, _) => Task.FromResult<Stream>(connected), null,
            CancellationToken.None);

        // The ChannelOpened reply passed the session's end check and is held mid-write.
        await HostChannelHarness.WriteFrameAsync(client, writer => BrokerProtocol.WriteOpenChannel(writer, 3, "C", "late"));
        await heldReply.Started.WaitAsync(HostChannelHarness.HangGuard);

        // The client goes away: the session ends and its drain starts (the drain's grace timer is
        // the second 5 second timer after the heartbeat sender's), holding only the request's task.
        await client.DisposeAsync();
        await clock.TimerCreated(JournalBrokerHost.ControlClosedGracePeriod, 2).WaitAsync(HostChannelHarness.HangGuard);

        // The reply completes, so the request tracks its channel task, which cannot finish while its
        // pipe is held open: the drain must take a second snapshot and wait for that task as well.
        heldReply.Finish();
        await connected.DisposalStarted.WaitAsync(HostChannelHarness.HangGuard);
        Assert.IsFalse(serve.IsCompleted, "The session cannot return while a channel it tracked is still running.");
        connected.FinishDisposal();
        await serve.WaitAsync(HostChannelHarness.HangGuard);
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> ParkedUntilCancelled(
        TaskCompletionSource stopped,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            stopped.TrySetResult();
        }

        yield break;
    }

    // A read that ignores its token: it reports the cancellation it was handed, then returns end of
    // stream once released, the way a read that lost the race against its cancellation does.
    sealed class UncancellableReadStream(Stream inner, TaskCompletionSource cancelled, Task release)
        : DelegatingStream(inner)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await release;
            return 0;
        }
    }

    // Wraps the control pipe's host end: its one write is started, reported and held, ignoring its
    // token, until Finish; then it goes through.
    sealed class HeldReply
    {
        readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Finish() => _finish.TrySetResult();

        public Stream Wrap(Stream inner) => new HeldReplyStream(inner, this);

        sealed class HeldReplyStream(Stream inner, HeldReply owner) : DelegatingStream(inner)
        {
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                owner._started.TrySetResult();
                await owner._finish.Task.WaitAsync(HostChannelHarness.HangGuard, CancellationToken.None);
                // The write is already under way, so it completes even though the session has ended.
                await base.WriteAsync(buffer, CancellationToken.None);
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => base.FlushAsync(CancellationToken.None);
        }
    }

    // Passes everything through except disposal, which starts, reports itself and waits to be finished.
    sealed class GatedDisposalStream(Stream inner) : DelegatingStream(inner)
    {
        readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DisposalStarted => _started.Task;

        public void FinishDisposal() => _finish.TrySetResult();

        public override async ValueTask DisposeAsync()
        {
            _started.TrySetResult();
            await _finish.Task.WaitAsync(HostChannelHarness.HangGuard);
            await base.DisposeAsync();
        }
    }

    // Streams it wraps pass reads through until BreakReads, which fails the read in flight and every
    // later one with the IOException a broken pipe throws.
    sealed class ReadBreaker : IDisposable
    {
        readonly CancellationTokenSource _broken = new();

        public Stream Wrap(string pipeName, Stream inner) => new ReadBreakingStream(inner, _broken.Token);

        public void BreakReads() => _broken.Cancel();

        public void Dispose() => _broken.Dispose();

        sealed class ReadBreakingStream(Stream inner, CancellationToken broken) : DelegatingStream(inner)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, broken);
                try
                {
                    return await base.ReadAsync(buffer, linked.Token);
                }
                catch (OperationCanceledException) when (broken.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Pipe is broken.");
                }
            }
        }
    }
}
