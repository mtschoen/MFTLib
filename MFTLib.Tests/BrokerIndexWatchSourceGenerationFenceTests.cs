using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issue 252: a stop whose wait for the broker's EndWatchAck is cut short by its token
///     releases the watch source, and the next stream on the same connection must not be ended by
///     that acknowledgement when it finally arrives. The scripted broker never acknowledges on its
///     own, so whether the acknowledgement is late is fixed by the script, not by time.
/// </summary>
[TestClass]
public sealed class BrokerIndexWatchSourceGenerationFenceTests
{
    static readonly IndexWatchTarget TargetC = new('C', 7, 100);

    [TestMethod]
    public async Task StopWhoseAcknowledgementWaitIsCancelled_LateAcknowledgement_LeavesTheNextStreamRunning()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);

        // The stop FileIndex.StopWatchingAsync makes: its token bounds the acknowledgement wait
        // through the teardown token the stream was started with.
        using var firstStreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stopCancellation = new CancellationTokenSource();
        var first = source.StartWatching([TargetC], static () => { }, stopCancellation.Token,
                firstStreamCancellation.Token)
            .GetAsyncEnumerator(firstStreamCancellation.Token);
        var firstMove = first.MoveNextAsync().AsTask();
        var firstStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, firstStart.Kind);

        await firstStreamCancellation.CancelAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await stopCancellation.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => firstMove.WaitAsync(token));
        await first.DisposeAsync();

        var second = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);
        var secondMove = second.MoveNextAsync().AsTask();
        var secondStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        Assert.IsTrue(secondStart.WatchGeneration > firstStart.WatchGeneration,
            $"Generation {secondStart.WatchGeneration} does not follow {firstStart.WatchGeneration}.");

        // The first watch's acknowledgement arrives now, ahead of a batch for the second watch.
        await harness.WriteAcknowledgementAsync(firstStart);
        await harness.WriteAsync(writer => BrokerProtocol.WriteJournalBatch(writer, "C",
            harness.ArmEpochForDrive(secondStart, 'C'), new UsnJournalCursor(7, 110),
            [JournalEntryFactory.Create(1, 105, "after-late-ack.txt")]));

        Assert.IsTrue(await secondMove.WaitAsync(token), "The late acknowledgement ended the next watch.");
        Assert.AreEqual("after-late-ack.txt", ((JournalBatch)second.Current).Entries[0].FileName);

        var secondEnd = second.MoveNextAsync().AsTask();
        await harness.WriteAcknowledgementAsync(secondStart);
        Assert.IsFalse(await secondEnd.WaitAsync(token), "The second watch's own acknowledgement did not end it.");
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task AcknowledgementOfTheRunningGeneration_EndsTheStream()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        var stream = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);

        var move = stream.MoveNextAsync().AsTask();
        var start = await harness.ReadFrameAsync();
        await harness.WriteAcknowledgementAsync(start);

        Assert.IsFalse(await move.WaitAsync(token), "The acknowledgement of the running generation did not end it.");
        await stream.DisposeAsync();
    }
}
