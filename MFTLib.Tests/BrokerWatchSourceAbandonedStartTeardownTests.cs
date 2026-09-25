using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issue 250 review findings: the teardown of a start abandoned mid-send must not hand
///     the connection to another watch without clean teardown. With generation fencing, an abandoned
///     start's stop cleans up the demux, and any late EndWatchAck for its generation is ignored by
///     the subsequent watch, so the connection remains safe to reuse. Genuine transport or cleanup
///     failures continue to fail subsequent starts.
/// </summary>
[TestClass]
public sealed class BrokerWatchSourceAbandonedStartTeardownTests
{
    static readonly IndexWatchTarget TargetC = new('C', 7, 100);

    [TestMethod]
    public async Task StartWaitingForAnAbandonedStart_ReusesConnectionAndIgnoresStaleAck()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        await using var blockedSend = await BlockedSend.HoldAsync(harness, token);

        var abandonedMove = await AbandonAStartMidSendAsync(source, token);
        var next = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);
        var nextMove = next.MoveNextAsync().AsTask();
        Assert.IsFalse(nextMove.IsCompleted, "A start went ahead while the abandoned send was still on the pipe.");

        blockedSend.Release();
        await abandonedMove.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var firstStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, firstStart.Kind);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);

        // Next start proceeds and writes its own StartWatch frame on the same connection.
        var secondStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        Assert.AreNotEqual(firstStart.WatchGeneration, secondStart.WatchGeneration);

        // Sending an ack for the first generation is ignored; the second start remains running.
        await harness.WriteAcknowledgementAsync(firstStart);

        // Deliver batch for the second start and verify it receives it.
        var cursor = new UsnJournalCursor(7UL, 110L);
        var entry = JournalEntryFactory.Create(1, 101, "fresh.txt");
        await harness.WriteAsync(writer =>
        {
            BrokerProtocol.WriteJournalBatch(writer, "C",
                harness.ArmEpochForDrive(secondStart, 'C'), cursor, [entry]);
        });

        Assert.IsTrue(await nextMove.WaitAsync(token));
        Assert.IsInstanceOfType<JournalBatch>(next.Current);

        var disposeTask = next.DisposeAsync().AsTask();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAcknowledgementAsync(secondStart);
        await disposeTask.WaitAsync(token);
    }

    [TestMethod]
    public async Task AbandonedStart_WithTransportFailureDuringTeardown_FailsTheNextStart()
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
        var startFrame = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, startFrame.Kind);

        // Break transport after StartWatch so the subsequent EndWatch write or stop fails.
        await harness.BreakTransportAsync();

        await retirement.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.IsTrue(retirement.IsCompletedSuccessfully);

        var next = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => next.MoveNextAsync().AsTask().WaitAsync(token));
        StringAssert.Contains(exception.Message, "tearing down a start that was cancelled");
        await next.DisposeAsync();
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
