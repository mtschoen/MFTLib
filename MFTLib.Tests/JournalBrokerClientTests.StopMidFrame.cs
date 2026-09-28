using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    /// <summary>
    ///     A stop whose acknowledgement wait is cancelled cancels the demux, and the broker is free to
    ///     be writing a frame at that moment. The demux must not stop between a frame's header and
    ///     its body: the next watch's demux would start reading in the middle of that frame and never
    ///     find a frame boundary again. The broker here writes a header, the stop is cancelled while
    ///     the demux waits for the body, and only then does the body arrive.
    /// </summary>
    [TestMethod]
    public async Task StopLiveWatchAsync_CancelledWhileTheDemuxIsInsideAFrame_LeavesTheNextWatchAtAFrameBoundary()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var observed = new ReadObservingStream(clientSide);
        await using var client = MakeMinimalFakeClient(observed);
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = hangGuard.Token;

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, token);
        var firstStart = await ReadOneFrameAsync(serverSide).WaitAsync(token);

        var caughtUp = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCaughtUp(caughtUp, "C", WatchSpecArmEpochs.ForDrive(firstStart, "C"));
        await serverSide.WriteAsync(caughtUp.WrittenMemory[..4], token);
        await serverSide.FlushAsync(token);
        // The demux's first read took the header; its second read is the one waiting for the body.
        await observed.ReadsIssued(2).WaitAsync(token);

        using var stopCancellation = new CancellationTokenSource();
        var stop = client.StopLiveWatchAsync(stopCancellation.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide).WaitAsync(token)).Kind);
        await stopCancellation.CancelAsync();
        await serverSide.WriteAsync(caughtUp.WrittenMemory[4..], token);
        await serverSide.FlushAsync(token);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => stop.WaitAsync(token));

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["D"] = new(8UL, 200L) }, token);
        var secondStart = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        var batches = client.CreateBatchSource()("D", new UsnJournalCursor(8UL, 200L), token)
            .GetAsyncEnumerator(token);
        var frames = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(frames, firstStart.WatchGeneration);
        BrokerProtocol.WriteJournalBatch(frames, "D", WatchSpecArmEpochs.ForDrive(secondStart, "D"),
            new UsnJournalCursor(8UL, 210L), [JournalEntryFactory.Create(1, 205, "after-cancelled-stop.txt")]);
        await serverSide.WriteAsync(frames.WrittenMemory, token);
        await serverSide.FlushAsync(token);

        Assert.IsTrue(await batches.MoveNextAsync(), "The second watch ended instead of delivering its batch.");
        Assert.AreEqual("after-cancelled-stop.txt", batches.Current.Entries[0].FileName);
        await batches.DisposeAsync();
    }

    /// <summary>
    ///     A broker that stays connected but stops sending in the middle of a frame must not hold a
    ///     cancelled stop: the rest of that frame may never come. The stop returns its cancellation,
    ///     and disposing the client still ends the reader that is waiting for the body.
    /// </summary>
    [TestMethod]
    public async Task StopLiveWatchAsync_CancelledWhileTheBrokerStallsInsideAFrame_Returns()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var observed = new ReadObservingStream(clientSide);
        // Disposed explicitly below: whether disposal finishes is part of what this test checks.
        var client = MakeMinimalFakeClient(observed);
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = hangGuard.Token;

        await StallInsideAFrameAsync(client, serverSide, observed, token);
        using var stopCancellation = new CancellationTokenSource();
        var stop = client.StopLiveWatchAsync(stopCancellation.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide).WaitAsync(token)).Kind);
        await stopCancellation.CancelAsync();

        await AssertFinishesAsync(stop, "The cancelled stop waited for a frame the broker never finished.", token);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => stop);
        await AssertFinishesAsync(client.DisposeAsync().AsTask(), "Disposal waited for the stalled frame.", token);
    }

    /// <summary>
    ///     After a cancelled stop left the reader inside a stalled frame, the next pipe user waits for
    ///     that frame within its own bound. When the bound runs out first, the connection is failed:
    ///     nothing may read on from the middle of that frame, so later calls fail plainly and the rest
    ///     of the frame, arriving late, is never taken for a frame of the next watch.
    /// </summary>
    [TestMethod]
    public async Task StartWatch_AfterAStopLeftAStalledFrame_FailsTheConnectionInsteadOfReadingMidFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var observed = new ReadObservingStream(clientSide);
        await using var client = MakeMinimalFakeClient(observed);
        string? deathReason = null;
        client.BrokerDied += reason => deathReason = reason;
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = hangGuard.Token;

        var unfinishedFrame = await StallInsideAFrameAsync(client, serverSide, observed, token);
        using var stopCancellation = new CancellationTokenSource();
        var stop = client.StopLiveWatchAsync(stopCancellation.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide).WaitAsync(token)).Kind);
        await stopCancellation.CancelAsync();
        await AssertFinishesAsync(stop, "The cancelled stop waited for a frame the broker never finished.", token);

        using var startCancellation = new CancellationTokenSource();
        var start = client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["D"] = new(8UL, 200L) }, startCancellation.Token);
        await startCancellation.CancelAsync();
        await AssertFinishesAsync(start, "The start waited past its own cancellation.", token);
        Assert.IsTrue(start.IsCanceled, "The start whose bound ran out must report its cancellation.");
        Assert.IsNotNull(deathReason, "Giving up on the stalled frame must fail the connection.");

        var frames = new ArrayBufferWriter<byte>();
        frames.Write(unfinishedFrame.Span);
        BrokerProtocol.WriteJournalBatch(frames, "D", 1U, new UsnJournalCursor(8UL, 210L),
            [JournalEntryFactory.Create(1, 205, "late.txt")]);
        await serverSide.WriteAsync(frames.WrittenMemory, token);
        await serverSide.FlushAsync(token);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["D"] = new(8UL, 200L) }, token));
    }

    // Starts a watch and has the broker send only the length prefix of a CaughtUp frame, leaving the
    // demux waiting for the body. Returns the body the broker has not sent.
    static async Task<ReadOnlyMemory<byte>> StallInsideAFrameAsync(
        JournalBrokerClient client, Stream serverSide, ReadObservingStream observed, CancellationToken token)
    {
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, token);
        var start = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        var caughtUp = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCaughtUp(caughtUp, "C", WatchSpecArmEpochs.ForDrive(start, "C"));
        await serverSide.WriteAsync(caughtUp.WrittenMemory[..4], token);
        await serverSide.FlushAsync(token);
        // The demux's first read took the header; its second read is the one waiting for the body.
        await observed.ReadsIssued(2).WaitAsync(token);
        return caughtUp.WrittenMemory[4..];
    }

    static async Task AssertFinishesAsync(Task operation, string message, CancellationToken hangGuard)
    {
        var finished = await Task.WhenAny(operation, Task.Delay(Timeout.InfiniteTimeSpan, hangGuard));
        Assert.AreSame(operation, finished, message);
    }
}
