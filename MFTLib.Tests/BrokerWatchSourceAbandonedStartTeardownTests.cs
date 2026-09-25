using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issue 250 review findings: the teardown of a start abandoned mid-send must not hand
///     the connection to another watch when its stop never read the broker's EndWatchAck, and its
///     failure must reach later starts whether or not one was already waiting. The scripted broker
///     never acknowledges the stop, so the timeout outcome is fixed by the script, not by time;
///     the timeout is set to zero only so the stop gives up at once. That timeout is process-wide,
///     hence <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BrokerWatchSourceAbandonedStartTeardownTests
{
    static readonly IndexWatchTarget TargetC = new('C', 7, 100);

    [TestMethod]
    public async Task StartWaitingForAnAbandonedStart_WhoseStopIsNeverAcknowledged_FailsInsteadOfWatching()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        await using var blockedSend = await BlockedSend.HoldAsync(harness, token);
        await using var scope = new ZeroAcknowledgementTimeout();

        var abandonedMove = await AbandonAStartMidSendAsync(source, token);
        var next = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);
        var nextMove = next.MoveNextAsync().AsTask();
        Assert.IsFalse(nextMove.IsCompleted, "A start went ahead while the abandoned send was still on the pipe.");

        blockedSend.Release();
        await abandonedMove.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);

        await AssertRefusedAsync(next, nextMove, token);
        Assert.IsTrue(harness.Client.LastStopTimedOut);

        // The acknowledgement the stop gave up on arrives late; no watch exists for it to end, and
        // the source still refuses to start one on this connection.
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await AssertStartRefusedAsync(source, token);
    }

    [TestMethod]
    public async Task AbandonedStart_WhoseStopIsNeverAcknowledged_FailsTheNextStartEvenAfterItsTeardownFinished()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var token = harness.CancellationToken;
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        await using var blockedSend = await BlockedSend.HoldAsync(harness, token);
        await using var scope = new ZeroAcknowledgementTimeout();

        var abandonedMove = await AbandonAStartMidSendAsync(source, token);
        var retirement = GetPrivateField<Task?>(source, "_abandonedStartRetirement");
        Assert.IsNotNull(retirement, "Abandoning the start mid-send must hand its send to a teardown.");

        blockedSend.Release();
        await abandonedMove.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await retirement.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // The teardown's failure is retained, not carried by the task, so there is no faulted task
        // left for the finalizer to report as unobserved when no start ever follows.
        Assert.IsTrue(retirement.IsCompletedSuccessfully, retirement.Exception?.ToString());
        await AssertStartRefusedAsync(source, token);
        await AssertStartRefusedAsync(source, token);
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

    static Task AssertStartRefusedAsync(BrokerIndexWatchSource source, CancellationToken token)
    {
        var start = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);
        return AssertRefusedAsync(start, start.MoveNextAsync().AsTask(), token);
    }

    /// <summary>
    ///     The start must fail naming the unacknowledged stop. A start that went ahead and is
    ///     watching instead is left undisposed, because disposing an enumerator with a pending move
    ///     throws and would hide this failure; the harness tears its pipe down either way.
    /// </summary>
    static async Task AssertRefusedAsync(IAsyncEnumerator<WatchStreamItem> start, Task<bool> move,
        CancellationToken token)
    {
        await ((Task)move.WaitAsync(token)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.IsTrue(move.IsFaulted, $"The start was not refused; its first move is {move.Status}.");
        var refusal = move.Exception!.InnerException;
        Assert.IsInstanceOfType<InvalidOperationException>(refusal, refusal?.ToString());
        Assert.IsInstanceOfType<TimeoutException>(refusal.InnerException, refusal.ToString());
        StringAssert.Contains(refusal.InnerException!.Message, "EndWatchAck");
        await start.DisposeAsync();
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

    /// <summary>Makes a stop give up on the acknowledgement at once, restoring the default afterwards.</summary>
    sealed class ZeroAcknowledgementTimeout : IAsyncDisposable
    {
        readonly TimeSpan _previous = JournalBrokerClient._endWatchAckTimeout;

        public ZeroAcknowledgementTimeout()
        {
            JournalBrokerClient._endWatchAckTimeout = TimeSpan.Zero;
        }

        public ValueTask DisposeAsync()
        {
            JournalBrokerClient._endWatchAckTimeout = _previous;
            return ValueTask.CompletedTask;
        }
    }
}
