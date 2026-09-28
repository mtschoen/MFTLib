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
}
