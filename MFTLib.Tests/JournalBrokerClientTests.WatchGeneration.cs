using System.Buffers;
using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issue 252: a stop bounded only by its token, and watch generations that keep a late
///     EndWatchAck from ending the next watch on the same connection. The scripted broker side of
///     the in-memory pipe decides when, or whether, anything is acknowledged, so no outcome here
///     depends on elapsed time; the ten-second token is a hang guard, not a measurement.
/// </summary>
public partial class JournalBrokerClientTests
{
    static readonly Dictionary<string, UsnJournalCursor> CursorC = new() { ["C"] = new(7UL, 100L) };

    [TestMethod]
    public async Task StopCancelledBeforeItsAck_ThenANewWatch_TheStaleAckLeavesTheNewWatchRunningAndItsOwnAckEndsIt()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = guard.Token;
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(CursorC, token);
        var firstStart = await ReadOneFrameAsync(serverSide).WaitAsync(token);

        using var stopCancellation = new CancellationTokenSource();
        var stop = client.StopLiveWatchAsync(stopCancellation.Token);
        var firstEnd = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.EndWatch, firstEnd.Kind);
        Assert.AreEqual(firstStart.WatchGeneration, firstEnd.WatchGeneration);
        await stopCancellation.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => stop.WaitAsync(token));

        // A new watch on the same client: no reconnect, and a later generation.
        await client.SendStartWatchAsync(CursorC, token);
        var secondStart = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        Assert.IsTrue(secondStart.WatchGeneration > firstStart.WatchGeneration);
        await using var batches = client.CreateBatchSource()("C", default, token).GetAsyncEnumerator(token);

        // The first watch's acknowledgement arrives now, followed by a batch for the second watch.
        // The demux reads frames in order, so the batch arriving proves the ack ended nothing.
        await WriteScriptedFramesAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteEndWatchAck(writer, firstStart.WatchGeneration);
            BrokerProtocol.WriteJournalBatch(writer, "C", WatchSpecArmEpochs.ForDrive(secondStart, "C"),
                new UsnJournalCursor(7UL, 110L), [JournalEntryFactory.Create(1, 105, "after-stale-ack.txt")]);
        }, token);
        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(token));
        Assert.AreEqual("after-stale-ack.txt", batches.Current.Entries.Single().FileName);

        // Its own acknowledgement still ends it, with no token needed.
        var secondStop = client.StopLiveWatchAsync(CancellationToken.None);
        var secondEnd = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        Assert.AreEqual(secondStart.WatchGeneration, secondEnd.WatchGeneration);
        await WriteScriptedFramesAsync(serverSide,
            writer => BrokerProtocol.WriteEndWatchAck(writer, secondEnd.WatchGeneration), token);
        await secondStop.WaitAsync(token);
        Assert.IsFalse(await batches.MoveNextAsync().AsTask().WaitAsync(token));
    }

    [TestMethod]
    public async Task StopWithATokenThatIsNeverCancelled_WaitsThroughAMismatchedAckForItsOwn()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = guard.Token;
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(CursorC, token);
        var start = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        await using var batches = client.CreateBatchSource()("C", default, token).GetAsyncEnumerator(token);

        using var neverCancelled = new CancellationTokenSource();
        var stop = client.StopLiveWatchAsync(neverCancelled.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide).WaitAsync(token)).Kind);

        // An acknowledgement for a generation this watch is not, then a batch that proves the demux
        // read past it: the stop is still waiting.
        await WriteScriptedFramesAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteEndWatchAck(writer, start.WatchGeneration + 1);
            BrokerProtocol.WriteJournalBatch(writer, "C", WatchSpecArmEpochs.ForDrive(start, "C"),
                new UsnJournalCursor(7UL, 110L), [JournalEntryFactory.Create(1, 105, "still-running.txt")]);
        }, token);
        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(token));
        Assert.IsFalse(stop.IsCompleted, "A stop ended on an acknowledgement for another generation.");

        await WriteScriptedFramesAsync(serverSide,
            writer => BrokerProtocol.WriteEndWatchAck(writer, start.WatchGeneration), token);
        await stop.WaitAsync(token);
    }

    [TestMethod]
    public async Task StopAgainstADeadBroker_EndsOnPipeEofWithoutAnyToken()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = guard.Token;
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(CursorC, token);
        await ReadOneFrameAsync(serverSide).WaitAsync(token);

        var stop = client.StopLiveWatchAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide).WaitAsync(token)).Kind);
        Assert.IsFalse(stop.IsCompleted, "The stop ended before the broker acknowledged or its pipe closed.");

        await serverSide.DisposeAsync(); // the broker dies without acknowledging
        await stop.WaitAsync(token);
    }

    [TestMethod]
    public async Task StopCancelledWhileAnotherOperationHoldsTheGate_TearsTheWatchDownBeforeTheNextStart()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = guard.Token;
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(CursorC, token);
        var firstStart = await ReadOneFrameAsync(serverSide).WaitAsync(token);

        var armOrderingGate = (SemaphoreSlim)typeof(JournalBrokerClient)
            .GetField("_armOrderingGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        await armOrderingGate.WaitAsync(token);
        Task nextStart;
        try
        {
            using var stopCancellation = new CancellationTokenSource();
            var stop = client.StopLiveWatchAsync(stopCancellation.Token);
            Assert.IsFalse(stop.IsCompleted, "The stop did not wait for the held gate.");
            await stopCancellation.CancelAsync();
            await AssertCancelledAsync(stop.WaitAsync(token));
            nextStart = client.SendStartWatchAsync(CursorC, token);
        }
        finally
        {
            armOrderingGate.Release();
        }

        await nextStart.WaitAsync(token);
        var deferredEnd = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.EndWatch, deferredEnd.Kind);
        Assert.AreEqual(firstStart.WatchGeneration, deferredEnd.WatchGeneration);
        var secondStart = await ReadOneFrameAsync(serverSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        Assert.IsTrue(secondStart.WatchGeneration > firstStart.WatchGeneration);
    }

    static async Task AssertCancelledAsync(Task task)
    {
        try
        {
            await task;
            Assert.Fail("The task completed instead of being cancelled.");
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException from the gate wait is an OperationCanceledException too.
        }
    }

    static async Task WriteScriptedFramesAsync(Stream serverSide, Action<ArrayBufferWriter<byte>> write,
        CancellationToken token)
    {
        var frames = new ArrayBufferWriter<byte>();
        write(frames);
        await serverSide.WriteAsync(frames.WrittenMemory, token);
        await serverSide.FlushAsync(token);
    }
}
