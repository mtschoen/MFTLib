using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.StartWatchingAsync" /> completes only once its session's source reports
///     that its stream accepts per-drive arm and disarm, so a rescan issued any time after the start
///     returns never meets a stream that has not started yet. Every ordering here is fixed by the
///     test through <see cref="ReadinessScriptedWatchSource" />, never by timing.
/// </summary>
[TestClass]
public class FileIndexWatchStartReadinessTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StartWatchingAsync_CompletesOnlyOnceTheSourceReportsReady_WithNoItemDelivered()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);

        var start = harness.Index.StartWatchingAsync(Token);
        var stream = await source.NextStreamAsync(Token);
        Assert.IsFalse(start.IsCompleted, "The start completed before its source was ready.");

        stream.ReportReady();
        await start.WaitAsync(FakeIndexWatchSource.HangGuard, Token);

        // Ready is about per-drive control, not data: nothing was delivered and the drive has
        // not caught up, yet a rescan disarms and re-arms it on the running stream.
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.Index.Drives[0].WatchCatchUp);
        await harness.Index.RescanAsync('T', Token);

        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, source.WatchOperations.ToArray());
        Assert.IsNull(harness.Index.Drives[0].WatchFailureMessage);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheSourceFailsBeforeReady_ThrowsThatFailureAndReleasesTheSession()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);
        var startupFailure = new IOException("the broker could not be reached");

        var start = harness.Index.StartWatchingAsync(Token);
        (await source.NextStreamAsync(Token)).Fail(startupFailure);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => start);
        Assert.AreSame(startupFailure, thrown);

        // Reported through the start, so a stop has nothing left to rethrow, and the session is
        // gone, so a fresh start is accepted and runs.
        await harness.Index.StopWatchingAsync(Token);
        var restart = harness.Index.StartWatchingAsync(Token);
        (await source.NextStreamAsync(Token)).ReportReady();
        await restart.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_CancelledBeforeReady_ThrowsAndReleasesTheSession()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var start = harness.Index.StartWatchingAsync(startCancellation.Token);
        var stream = await source.NextStreamAsync(Token);
        await startCancellation.CancelAsync();

        await AssertCancelledAsync(start);
        await stream.Ended;
        Assert.IsTrue(stream.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.Index.Drives[0].WatchCatchUp);

        var restart = harness.Index.StartWatchingAsync(Token);
        (await source.NextStreamAsync(Token)).ReportReady();
        await restart.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_DuringStartup_CancelsTheStart()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);

        var start = harness.Index.StartWatchingAsync(Token);
        var stream = await source.NextStreamAsync(Token);
        await harness.Index.StopWatchingAsync(Token);

        await AssertCancelledAsync(start);
        await stream.Ended;
        var restart = harness.Index.StartWatchingAsync(Token);
        (await source.NextStreamAsync(Token)).ReportReady();
        await restart.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task DisposeAsync_DuringStartup_CancelsTheStart()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);

        var start = harness.Index.StartWatchingAsync(Token);
        var stream = await source.NextStreamAsync(Token);
        await harness.Index.DisposeAsync();

        await AssertCancelledAsync(start);
        await stream.Ended;
    }

    [TestMethod]
    public async Task StartWatchingAsync_IsNotSatisfiedByAnEarlierSessionsReadiness()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);
        var first = harness.Index.StartWatchingAsync(Token);
        var firstStream = await source.NextStreamAsync(Token);
        firstStream.ReportReady();
        await first.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        await harness.Index.StopWatchingAsync(Token);

        var second = harness.Index.StartWatchingAsync(Token);
        var secondStream = await source.NextStreamAsync(Token);
        firstStream.ReportReadyWithoutGoingLive();
        Assert.IsFalse(second.IsCompleted, "A finished session's readiness completed a later start.");

        secondStream.ReportReady();
        await second.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledWhileTheSessionIsStarting_LeavesTheDriveUntouched()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);
        using var rescanCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var start = harness.Index.StartWatchingAsync(Token);
        var stream = await source.NextStreamAsync(Token);
        var rescan = harness.Index.RescanAsync('T', rescanCancellation.Token);
        await rescanCancellation.CancelAsync();

        // The rescan was waiting for the session to become ready and had touched nothing, so it
        // records no failure against a drive the starting session is about to watch.
        await AssertCancelledAsync(rescan);
        Assert.IsNull(harness.Index.Drives[0].WatchFailureMessage);
        Assert.AreEqual(0, source.WatchOperations.Count);

        stream.ReportReady();
        await start.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.Index.Drives[0].WatchCatchUp);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_OntoASessionThatStartedWhileItsProducerRan_ArmsOnlyOnceThatSessionIsReady()
    {
        var source = new ReadinessScriptedWatchSource();
        using var harness = new WatchHarness(source);
        var productionHold = harness.HoldNextProduction('T');
        var produced = harness.ObserveNextProducedBlock('T');

        var rescan = harness.Index.RescanAsync('T', Token);
        await productionHold.Entered.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        var start = harness.Index.StartWatchingAsync(Token);
        var stream = await source.NextStreamAsync(Token);
        productionHold.Release();
        await produced.WaitAsync(FakeIndexWatchSource.HangGuard, Token);

        stream.ReportReady();
        await start.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard, Token);

        CollectionAssert.AreEqual(new[] { "arm:T" }, source.WatchOperations.ToArray());
        Assert.IsNull(harness.Index.Drives[0].WatchFailureMessage);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WithASourceThatReportsNoReadiness_CompletesOnceTheSourceIsRunning()
    {
        using var harness = new WatchHarness();

        await harness.Index.StartWatchingAsync(Token);

        // The default readiness closes the interval before the pump invokes the source: by the
        // time the start returns, the source's iterator is running and answers per-drive calls,
        // so an immediate rescan disarms and re-arms instead of being rejected.
        Assert.AreEqual(1, harness.SourceInvocationCount);
        Assert.AreEqual(1, harness.LiveSourceCount);
        await harness.Index.RescanAsync('T', Token);
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.IsNull(harness.Index.Drives[0].WatchFailureMessage);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WithASourceThatReportsNoReadiness_CompletesAtTheStreamsFirstIncompleteAwait()
    {
        var source = new AwaitsBeforeGoingLiveWatchSource();
        using var harness = new WatchHarness(source);

        // The documented limit of the default: it cannot see past the stream's first incomplete
        // await, so a source that connects before going live is reported ready while connecting.
        await harness.Index.StartWatchingAsync(Token);
        await source.Connecting.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.IsFalse(source.IsLive);

        source.FinishConnecting.TrySetResult();
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WithASourceThatReportsNoReadiness_WhoseFirstMoveEndsSynchronously_Throws()
    {
        var source = new SynchronousFirstMoveWatchSource(failure: null);
        using var harness = new WatchHarness(source);

        // The stream ended before it could accept any per-drive call, so the default readiness
        // must not report it ready: the start fails as a stream that ended before ready does.
        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StartWatchingAsync(Token));

        StringAssert.Contains(thrown.Message, "before it was ready");
        await AssertStartableAgainAsync(harness, source);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WithASourceThatReportsNoReadiness_WhoseFirstMoveFaultsSynchronously_ThrowsTheFault()
    {
        var startupFailure = new IOException("the source failed on its first move");
        var source = new SynchronousFirstMoveWatchSource(startupFailure);
        using var harness = new WatchHarness(source);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StartWatchingAsync(Token));

        Assert.AreSame(startupFailure, thrown);
        await AssertStartableAgainAsync(harness, source);
    }

    /// <summary>
    ///     The failed start released its session, and reported its failure itself: a stop has
    ///     nothing to rethrow and a fresh start is accepted rather than refused as already watching.
    /// </summary>
    static async Task AssertStartableAgainAsync(WatchHarness harness, SynchronousFirstMoveWatchSource source)
    {
        await harness.Index.StopWatchingAsync(CancellationToken.None);
        source.GoLiveOnNextStart();
        await harness.Index.StartWatchingAsync(CancellationToken.None);
        Assert.AreEqual(2, source.StartCount);
        await harness.Index.StopWatchingAsync(CancellationToken.None);
    }

    static async Task AssertCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(FakeIndexWatchSource.HangGuard);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("The task completed instead of being cancelled.");
    }

    /// <summary>
    ///     A source that implements only the plain overload and awaits a connection before it
    ///     goes live, which is the shape the default readiness cannot see into.
    /// </summary>
    sealed class AwaitsBeforeGoingLiveWatchSource : IIndexWatchSource
    {
        readonly TaskCompletionSource _connecting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FinishConnecting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Connecting => _connecting.Task;

        public bool IsLive { get; private set; }

        public async IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _connecting.TrySetResult();
            await FinishConnecting.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            IsLive = true;
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    ///     A source implementing only the plain overload whose first <c>MoveNextAsync</c> completes
    ///     synchronously: ending the stream, or faulting it, before the iterator reaches any await.
    ///     After <see cref="GoLiveOnNextStart" /> its next stream waits for cancellation instead.
    /// </summary>
    sealed class SynchronousFirstMoveWatchSource(Exception? failure) : IIndexWatchSource
    {
        bool _goLive;
        int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);

        public void GoLiveOnNextStart() => _goLive = true;

        public async IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            if (_goLive)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (failure is not null)
            {
                throw failure;
            }

            yield break;
        }

        public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
