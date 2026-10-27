using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class FileIndexPerDriveWatchTests
{
    [ThreadStatic] static bool _settlingPumpFault;

    [TestMethod]
    public async Task RestartAfterStop_AwaitsOldDrain_BeforePublishing()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var oldHandle = harness.Source.WatchFor('T');
        var applying = harness.HoldFirstApply('T');
        _ = oldHandle.Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.IndexFor('T').StopWatchingAsync(alreadyCancelled.Token));

        var restart = harness.IndexFor('T').StartWatchingAsync(Token);

        Assert.HasCount(1, harness.Source.Starts, "the restart waits for the old instance to drain");
        Assert.IsFalse(restart.IsCompleted);
        applying.Release();
        await restart.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.HasCount(2, harness.Source.Starts);
        Assert.AreEqual(1, oldHandle.DisposeCount, "the old pump disposed its handle before the restart published");
    }

    [TestMethod]
    public async Task RetiringPumpBatch_IsDroppedNotApplied()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var generationBefore = harness.BlockFor('T').Header.Generation;
        var applying = harness.HoldFirstApply('T');
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.IndexFor('T').StopWatchingAsync(alreadyCancelled.Token));
        var restart = harness.IndexFor('T').StartWatchingAsync(Token);

        applying.Release();
        await restart.WaitAsync(HangGuard, TestContext.CancellationToken);

        var header = harness.BlockFor('T').Header;
        Assert.AreEqual(generationBefore, header.Generation, "the retiring pump's batch never reached the block");
        Assert.AreEqual(WatchHarness.NextUsn, header.UsnNextUsn);
        Assert.IsEmpty(harness.Changes);
        Assert.AreEqual(WatchHarness.NextUsn, harness.Source.TargetsFor('T')[1].NextUsn,
            "the successor was armed from the cursor the dropped batch would have moved");
    }

    [TestMethod]
    public async Task OldInstanceFault_AfterRestart_DoesNotTouchNewInstance()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var oldHandle = harness.Source.WatchFor('T');
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('T').StartWatchingAsync(Token);

        oldHandle.LoseChannel(new IOException("the old pipe broke late"));
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "fresh.txt"));

        Assert.IsEmpty(harness.Faults);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task RetiredInstanceFault_IsNotRecorded()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        handle.FailOnCancellation(new IOException("the pipe closed under the pending read"));

        await harness.IndexFor('T').StopWatchingAsync(Token);

        Assert.IsEmpty(harness.Faults, "a stopped watch's last error belongs to no current watch");
        var drive = harness.DriveFor('T');
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.AreEqual(WatchState.NotStarted, drive.Watch.State);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task Rescan_TokenCancelledAfterCommit_DrainsAndStartsTheReplacement()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var applying = harness.HoldFirstApply('T');
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        using var rescanCancellation = new CancellationTokenSource();
        var rescan = harness.IndexFor('T').RescanAsync(rescanCancellation.Token);

        await rescanCancellation.CancelAsync();
        applying.Release();

        await Assert.ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));
        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(2, starts, "cancellation after commit does not abandon the replacement watch");
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), WatchHarness.JournalIdentifier, WatchHarness.NextUsn), starts[1]);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        await harness.IndexFor('V').StartWatchingAsync(Token);
        var draining = harness.HoldFirstApply('U');
        _ = harness.Source.WatchFor('U').Queue(WatchHarness.Batch(10, "u.txt"));
        await draining.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        using var waitCancellation = new CancellationTokenSource();
        using var stopCancellation = new CancellationTokenSource();
        var waiter = ObserveAsync(harness.IndexFor('V').WaitForCatchUpAsync(waitCancellation.Token));
        var stopper = ObserveAsync(harness.IndexFor('U').StopWatchingAsync(stopCancellation.Token));
        harness.Index.Changed += CancelWhileFlagged("t.txt", waitCancellation, stopCancellation);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "t.txt"));

        var observed = await Task.WhenAll(waiter, stopper).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(observed[0], "the catch-up wait's continuation ran on the cancelling handler's stack");
        Assert.IsFalse(observed[1], "the stop's continuation ran on the cancelling handler's stack");
        draining.Release();
        return;

        static async Task<bool> ObserveAsync(Task operation)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            return _settlingPumpFault;
        }
    }

    /// <summary>
    ///     A <see cref="FileIndex.Changed" /> handler that, for the change named
    ///     <paramref name="fileName" />, cancels every source with the thread-static flag set, so an
    ///     awaiter whose continuation runs inline on that stack records it.
    /// </summary>
    static Action<FileChange> CancelWhileFlagged(string fileName, params CancellationTokenSource[] sources)
    {
        return change =>
        {
            if (change.Entry.Name != fileName)
            {
                return;
            }

            _settlingPumpFault = true;
            try
            {
                foreach (var source in sources)
                {
                    source.Cancel();
                }
            }
            finally
            {
                _settlingPumpFault = false;
            }
        };
    }

    [TestMethod]
    public async Task RescanAsync_CancelledWhileTheWatchIsStarting_LeavesTheDriveUntouched()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        using var rescanCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var start = harness.IndexFor('T').StartWatchingAsync(Token);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var productionsBefore = harness.ProductionCount('T');

        var rescan = harness.IndexFor('T').RescanAsync(rescanCancellation.Token);
        await rescanCancellation.CancelAsync();

        // The rescan was waiting for the starting watch's lifecycle gate and had touched nothing, so
        // it scans nothing and records no failure against a drive the start is about to watch.
        await Assert.ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(productionsBefore, harness.ProductionCount('T'));

        held.Release();
        await start.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhileARescanProduces_WatchesOnceFromTheFreshCursor()
    {
        using var harness = new WatchHarness();
        var producing = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await producing.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var start = harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.IsFalse(start.IsCompleted, "the start waits for the rescan's lifecycle gate");
        Assert.IsEmpty(harness.Source.Starts);
        producing.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await start.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSequenceEqual([new IndexWatchTarget(new VolumeKey('T'), 13, 9000)], [.. harness.Source.Targets]);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task Stop_DuringRescan_ReturnsWhileScanRuns_AndRescanDoesNotRestart()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producing = harness.HoldNextProduction('T');
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await producing.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        producing.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task Rescan_RestartsWatchFromFreshCursor()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var firstHandle = harness.Source.WatchFor('T');
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);

        await harness.IndexFor('T').RescanAsync(Token);

        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(2, starts);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9000), starts[1]);
        Assert.AreEqual(1, firstHandle.DisposeCount);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task Dispose_StopsEveryDriveAndNeverThrows()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        await Task.WhenAll(
            harness.IndexFor('T').DisposeAsync().AsTask(),
            harness.IndexFor('U').DisposeAsync().AsTask()).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, harness.Source.Watches);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount, $"drive {handle.Drive}");
        }
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterStop()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');

        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterDriveFault()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        handle.FailDrive(new IOException("the volume went away"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await handle.Disposed.WaitAsync(HangGuard, TestContext.CancellationToken);

        await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterIndexDisposal()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');

        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task WaitForCatchUp_DisposalTokenCancelled_ThrowsOperationCanceled()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        // The disposal token alone, without the rest of disposal, so the slot is observable after.
        var disposalCancellation = (CancellationTokenSource)typeof(FileIndex)
            .GetField("_disposalCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(harness.Index)!;
        await disposalCancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task PumpFaultSettlesWaiter_ContinuationNotInline()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        harness.Index.Hooks.PumpFaultSettlementWrapper = settle =>
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
        var waiter = WaitThenStopOtherDriveAsync(harness.Index, harness.IndexFor('U'));

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));

        Assert.IsFalse(await waiter.WaitAsync(HangGuard, TestContext.CancellationToken), "the waiter's continuation ran on the settling stack");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('U').Watch.State);
        return;

        async Task<bool> WaitThenStopOtherDriveAsync(FileIndex index, FileIndex otherIndex)
        {
            await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => index.WaitForCatchUpAsync(Token));
            var observedWhileSettling = _settlingPumpFault;
            await otherIndex.StopWatchingAsync(Token);
            return observedWhileSettling;
        }
    }

}
