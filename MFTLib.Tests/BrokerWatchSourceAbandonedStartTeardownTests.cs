using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issues 250 and 252: the teardown of a start abandoned mid-send stops the watch that
///     send started under the same generation rule as every other stop. A later start supersedes the
///     teardown's wait for the broker's EndWatchAck and watches on the same connection, and the late
///     acknowledgement cannot end it; with no later start the teardown waits for the acknowledgement.
///     The scripted broker decides when, or whether, it acknowledges, so no outcome depends on time.
/// </summary>
[TestClass]
public sealed class BrokerWatchSourceAbandonedStartTeardownTests
{
    static readonly IndexWatchTarget TargetC = new('C', 7, 100);

    [TestMethod]
    public async Task StartWaitingForAnAbandonedStart_SupersedesItsUnacknowledgedStop_AndALateAckCannotEndIt()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        await using var blockedSend = await BlockedSend.HoldAsync(harness, token);

        var abandonedMove = await AbandonAStartMidSendAsync(source, token);
        using var nextCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var next = source.StartWatching([TargetC], nextCancellation.Token)
            .GetAsyncEnumerator(nextCancellation.Token);
        var nextMove = next.MoveNextAsync().AsTask();
        Assert.IsFalse(nextMove.IsCompleted, "A start went ahead while the abandoned send was still on the pipe.");

        blockedSend.Release();
        await abandonedMove.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var abandonedStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, abandonedStart.Kind);
        var abandonedEnd = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, abandonedEnd.Kind);
        Assert.AreEqual(abandonedStart.WatchGeneration, abandonedEnd.WatchGeneration);

        // The later start did not wait for an acknowledgement the broker never sent.
        var nextStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, nextStart.Kind);
        Assert.IsTrue(nextStart.WatchGeneration > abandonedStart.WatchGeneration);

        // The abandoned watch's acknowledgement arrives late, ahead of a batch for the new watch.
        // Frames are read in order, so the batch arriving proves the acknowledgement ended nothing.
        var entry = JournalEntryFactory.Create(1, 105, "after-late-ack.txt");
        await harness.WriteAsync(writer =>
        {
            BrokerProtocol.WriteEndWatchAck(writer, abandonedStart.WatchGeneration);
            BrokerProtocol.WriteJournalBatch(writer, "C", harness.ArmEpochForDrive(nextStart, 'C'),
                new UsnJournalCursor(7, 110), [entry]);
        });
        Assert.IsTrue(await nextMove.WaitAsync(token));
        Assert.AreEqual("after-late-ack.txt", ((JournalBatch)next.Current).Entries.Single().FileName);

        // Its own acknowledgement still ends it.
        await nextCancellation.CancelAsync();
        var finalMove = next.MoveNextAsync().AsTask();
        var nextEnd = await harness.AcknowledgeEndWatchAsync();
        Assert.AreEqual(nextStart.WatchGeneration, nextEnd.WatchGeneration);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => finalMove.WaitAsync(token));
    }

    [TestMethod]
    public async Task AbandonedStart_WithNoLaterStart_WaitsForItsAckAndThenReleasesTheSource()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        await using var blockedSend = await BlockedSend.HoldAsync(harness, token);

        var abandonedMove = await AbandonAStartMidSendAsync(source, token);
        var retirement = GetPrivateField<Task?>(source, "_abandonedStartRetirement");
        Assert.IsNotNull(retirement, "Abandoning the start mid-send must hand its send to a teardown.");

        blockedSend.Release();
        await abandonedMove.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        var endWatch = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, endWatch.Kind);
        Assert.IsFalse(retirement.IsCompleted, "The teardown finished without the acknowledgement.");

        await harness.WriteAsync(writer => BrokerProtocol.WriteEndWatchAck(writer, endWatch.WatchGeneration));
        await retirement.WaitAsync(token);
        Assert.IsTrue(retirement.IsCompletedSuccessfully, retirement.Exception?.ToString());

        using var nextCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var next = source.StartWatching([TargetC], nextCancellation.Token)
            .GetAsyncEnumerator(nextCancellation.Token);
        var nextMove = next.MoveNextAsync().AsTask();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        await nextCancellation.CancelAsync();
        await harness.AcknowledgeEndWatchAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => nextMove.WaitAsync(token));
    }

    /// <summary>
    ///     Starts a stream whose StartWatch send parks behind the held arm-ordering gate, then
    ///     cancels it. Everything up to that gate completes synchronously inside the first
    ///     <c>MoveNextAsync</c> call, so the start is provably inside its send when it is cancelled.
    /// </summary>
    static async Task<Task> AbandonAStartMidSendAsync(BrokerIndexWatchSource source, CancellationToken token)
    {
        using var abandonedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var abandoned = source.StartWatching([TargetC], abandonedCancellation.Token)
            .GetAsyncEnumerator(abandonedCancellation.Token);
        var abandonedMove = abandoned.MoveNextAsync().AsTask();
        Assert.IsFalse(abandonedMove.IsCompleted, "The start did not park inside its StartWatch send.");

        await abandonedCancellation.CancelAsync();
        try
        {
            await abandonedMove.WaitAsync(token);
            Assert.Fail("The abandoned start completed instead of being cancelled.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // The start stopped waiting while its send is still held, which is the setup.
        }

        await abandoned.DisposeAsync();
        return abandonedMove;
    }

    static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(instance.GetType().Name, fieldName);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>Holds the client's arm-ordering gate, released at most once whichever way the test ends.</summary>
    sealed class BlockedSend : IAsyncDisposable
    {
        readonly SemaphoreSlim _armOrderingGate;
        int _released;

        BlockedSend(SemaphoreSlim armOrderingGate)
        {
            _armOrderingGate = armOrderingGate;
        }

        public static async Task<BlockedSend> HoldAsync(ScriptedWatchBrokerHarness harness, CancellationToken token)
        {
            var armOrderingGate = GetPrivateField<SemaphoreSlim>(harness.Client, "_armOrderingGate");
            await armOrderingGate.WaitAsync(token);
            return new BlockedSend(armOrderingGate);
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _armOrderingGate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }
}
