using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     The single-root lifecycle entry points (<see cref="FileIndex.StartWatchingAsync" />,
///     <see cref="FileIndex.StopWatchingAsync" />, <see cref="FileIndex.RescanAsync" />, and
///     <see cref="FileIndex.WaitForCatchUpAsync" />): each returns a <see cref="RootOperationResult" />
///     for the index's root and throws only for disposal or caller cancellation. Ordering is proven
///     with <see cref="TestGate" /> holds and scripted producers, never with time.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexBatchedOperationTests
{
    [ThreadStatic] static bool _settlingPumpFault;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task StartWatching_SucceededFailedAndNotApplicable_ReportsPerRoot_DoesNotThrow()
    {
        using var harness = WatchHarness.WithBlocklessRoots(['V'], 'T', 'U', 'V');
        var failure = new IOException("the broker refused U");
        harness.Source.FailNextStartFor('U', failure);

        var resultT = await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var resultU = await harness.IndexFor('U').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var resultV = await harness.IndexFor('V').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, resultT.Outcome);
        Assert.IsNull(resultT.Failure);
        Assert.AreEqual(RootOperationOutcome.Failed, resultU.Outcome);
        Assert.AreSame(failure, resultU.Failure);
        Assert.AreEqual(RootOperationOutcome.NotApplicable, resultV.Outcome);
        Assert.IsNull(resultV.Failure);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.IsEmpty(harness.Source.TargetsFor('V'), "a root with no block never reaches the source");
        Assert.IsEmpty(harness.Faults, "a failed source start raises no WatchFaulted");
    }

    [TestMethod]
    public async Task StartWatching_CancelledWhileGated_ThrowsAndPublishesNoHandle()
    {
        using var harness = new WatchHarness();
        var heldStart = harness.TrackGate();
        harness.Source.HoldStartFor('U', heldStart, observeToken: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var start = harness.IndexFor('U').StartWatchingAsync(cancellation.Token);
        try
        {
            await heldStart.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

            await cancellation.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);

            await Assert.ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard, TestContext.CancellationToken));
        }
        finally
        {
            heldStart.Release();
        }

        Assert.HasCount(1, harness.Source.TargetsFor('U'), "U's start reached the source and was cancelled there");
        Assert.IsEmpty(harness.Source.Watches, "no handle was published");
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('U').Watch.State,
            "a start cancelled in flight leaves the root with no running watch");
    }

    [TestMethod]
    public async Task Lifecycle_CancelledToken_StillThrows()
    {
        using var harness = new WatchHarness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);
        var index = harness.Index;

        await Assert.ThrowsAsync<OperationCanceledException>(() => index.StartWatchingAsync(cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.StopWatchingAsync(cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.RescanAsync(cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.WaitForCatchUpAsync(cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Lifecycle_Disposed_ThrowsObjectDisposed()
    {
        using var harness = new WatchHarness();
        var index = harness.Index;
        await index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.StartWatchingAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.StopWatchingAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.RescanAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.WaitForCatchUpAsync(Token));
    }

    [TestMethod]
    public async Task StopWatching_ReturnsFaultAsFailed()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('U').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var faulted = harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        harness.Source.WatchFor('T').FailVolume(new IOException("the volume went away"));
        var fault = await faulted.WaitAsync(HangGuard, TestContext.CancellationToken);

        var resultT = await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var resultU = await harness.IndexFor('U').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var resultV = await harness.IndexFor('V').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Failed, resultT.Outcome);
        Assert.AreSame(fault.Exception, resultT.Failure, "stop carries the fault that had ended T's watch");
        Assert.AreEqual(RootOperationOutcome.Succeeded, resultU.Outcome);
        Assert.AreEqual(RootOperationOutcome.NotApplicable, resultV.Outcome, "V was never watching");
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task WaitForCatchUp_SettledByPumpFault_ContinuationNotInline()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('U').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        harness.IndexFor('T').Hooks.PumpFaultSettlementWrapper = settle =>
        {
            _settlingPumpFault = true;
            try
            {
                settle();
            }
            finally
            {
                _settlingPumpFault = false;
            }
        };
        var waiter = WaitThenStopOtherRootAsync(harness);

        harness.Source.WatchFor('T').FailVolume(new IOException("the volume went away"));

        Assert.IsFalse(await waiter.WaitAsync(HangGuard, TestContext.CancellationToken), "the waiter's continuation ran on the settling stack");
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('U').Watch.State);
        return;

        async Task<bool> WaitThenStopOtherRootAsync(WatchHarness watchHarness)
        {
            var result = await watchHarness.IndexFor('T').WaitForCatchUpAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome);
            var observedWhileSettling = _settlingPumpFault;
            await watchHarness.IndexFor('U').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
            return observedWhileSettling;
        }
    }

    [TestMethod]
    public async Task Rescan_OneRootStopsAfterThreeLostCatchUps_ReportsFailedWithMessage_OtherSucceeds()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        var resultT = await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var resultU = await harness.IndexFor('U').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Failed, resultT.Outcome);
        var lost = (JournalCatchUpLostException)resultT.Failure!;
        Assert.IsTrue(lost.IsRecoveryStopped);
        Assert.IsTrue(harness.RootFor('T').Watch.IsRecoveryStopped);
        Assert.IsFalse(harness.RootFor('U').Watch.IsRecoveryStopped);
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.Contains("12288", lost.Message, "the message carries the journal size to grow to");
        Assert.AreEqual(RootOperationOutcome.Succeeded, resultU.Outcome);
        Assert.AreEqual(RootState.Ready, harness.RootFor('U').State);
    }

    [TestMethod]
    public async Task SingleRescan_CatchUpStop_ReportsJournalCatchUpLostFailure()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.Contains("12288", failure.Message);
    }

    [TestMethod]
    public async Task SingleRescan_ProducerReturnsNoBlock_ReportsFailedResultWithFailureMessage()
    {
        using var harness = new WatchHarness('T');
        var producerFailure = new IOException("the scan could not read the volume");
        harness.FailNextProduction('T', producerFailure);

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("the scan could not read the volume", failure.Message);
        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.AreEqual("the scan could not read the volume", harness.RootFor('T').Failure?.Message);
    }
}
