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
        await harness.Index.StartWatchingAsync('T', Token);
        var oldHandle = harness.Source.HandleFor('T');
        var applying = HoldFirstApply(harness, 'T');
        _ = oldHandle.Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard);
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();
        await ThrowsAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync('T', alreadyCancelled.Token));

        var restart = harness.Index.StartWatchingAsync('T', Token);

        Assert.AreEqual(1, harness.Source.Starts.Count, "the restart waits for the old instance to drain");
        Assert.IsFalse(restart.IsCompleted);
        applying.Release();
        await restart.WaitAsync(HangGuard);
        Assert.AreEqual(2, harness.Source.Starts.Count);
        Assert.AreEqual(1, oldHandle.DisposeCount, "the old pump disposed its handle before the restart published");
    }

    [TestMethod]
    public async Task RetiringPumpBatch_IsDroppedNotApplied()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var generationBefore = harness.BlockFor('T').Header.Generation;
        var applying = HoldFirstApply(harness, 'T');
        _ = harness.Source.HandleFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard);
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();
        await ThrowsAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync('T', alreadyCancelled.Token));
        var restart = harness.Index.StartWatchingAsync('T', Token);

        applying.Release();
        await restart.WaitAsync(HangGuard);

        var header = harness.BlockFor('T').Header;
        Assert.AreEqual(generationBefore, header.Generation, "the retiring pump's batch never reached the block");
        Assert.AreEqual(WatchHarness.NextUsn, header.UsnNextUsn);
        Assert.AreEqual(0, harness.Changes.Count);
        Assert.AreEqual(WatchHarness.NextUsn, harness.Source.StartsFor('T')[1].NextUsn,
            "the successor was armed from the cursor the dropped batch would have moved");
    }

    [TestMethod]
    public async Task OldInstanceFault_AfterRestart_DoesNotTouchNewInstance()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var oldHandle = harness.Source.HandleFor('T');
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('T', Token);

        oldHandle.LoseChannel(new IOException("the old pipe broke late"));
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(9, "fresh.txt"));

        Assert.AreEqual(0, harness.Faults.Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
    }

    [TestMethod]
    public async Task RetiredInstanceFault_IsNotRecorded()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        handle.FailOnCancellation(new IOException("the pipe closed under the pending read"));

        await harness.Index.StopWatchingAsync('T', Token);

        Assert.AreEqual(0, harness.Faults.Count, "a stopped watch's last error belongs to no current watch");
        var drive = harness.DriveFor('T');
        Assert.IsNull(drive.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.WatchCatchUp);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task Rescan_CancelledWhileTheOldWatchDrains_RestartsTheHealthyWatch()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var applying = HoldFirstApply(harness, 'T');
        _ = harness.Source.HandleFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard);
        using var rescanCancellation = new CancellationTokenSource();
        var rescan = harness.Index.RescanAsync('T', rescanCancellation.Token);

        await rescanCancellation.CancelAsync();
        applying.Release();

        await ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard));
        var starts = harness.Source.StartsFor('T');
        Assert.AreEqual(2, starts.Count, "a healthy watch resumes after a failed scan, cancellation included");
        Assert.AreEqual(new IndexWatchTarget('T', WatchHarness.JournalId, WatchHarness.NextUsn), starts[1]);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        await harness.Index.StartWatchingAsync('V', Token);
        var draining = HoldFirstApply(harness, 'U');
        _ = harness.Source.HandleFor('U').Queue(WatchHarness.Batch(10, "u.txt"));
        await draining.Entered.WaitAsync(HangGuard);
        using var waitCancellation = new CancellationTokenSource();
        using var stopCancellation = new CancellationTokenSource();
        bool? waitObserved = null;
        bool? stopObserved = null;
        var waiter = ObserveAsync(harness.Index.WaitForCatchUpAsync('V', waitCancellation.Token),
            observed => waitObserved = observed);
        var stopper = ObserveAsync(harness.Index.StopWatchingAsync('U', stopCancellation.Token),
            observed => stopObserved = observed);
        harness.Index.Changed += CancelWhileFlagged("t.txt", waitCancellation, stopCancellation);

        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(9, "t.txt"));

        await Task.WhenAll(waiter, stopper).WaitAsync(HangGuard);
        Assert.IsFalse(waitObserved, "the catch-up wait's continuation ran on the cancelling handler's stack");
        Assert.IsFalse(stopObserved, "the stop's continuation ran on the cancelling handler's stack");
        draining.Release();
        return;

        static async Task ObserveAsync(Task operation, Action<bool> record)
        {
            await ThrowsAsync<OperationCanceledException>(() => operation);
            record(_settlingPumpFault);
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
    public async Task Rescan_HoldsLifecycleGateThroughProduction()
    {
        using var harness = new WatchHarness();
        var producing = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await producing.Entered.WaitAsync(HangGuard);

        var start = harness.Index.StartWatchingAsync('T', Token);

        Assert.IsFalse(start.IsCompleted, "the start waits for the rescan's lifecycle gate");
        Assert.AreEqual(0, harness.Source.Starts.Count);
        producing.Release();
        await rescan.WaitAsync(HangGuard);
        await start.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Source.Starts.Count);
    }

    [TestMethod]
    public async Task Stop_DuringRescan_ReturnsWhileScanRuns_AndRescanDoesNotRestart()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var producing = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await producing.Entered.WaitAsync(HangGuard);

        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);

        producing.Release();
        await rescan.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Source.StartsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task Rescan_RestartsWatchFromFreshCursor()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var firstHandle = harness.Source.HandleFor('T');
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);

        await harness.Index.RescanAsync('T', Token);

        var starts = harness.Source.StartsFor('T');
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), starts[1]);
        Assert.AreEqual(1, firstHandle.DisposeCount);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task Dispose_StopsEveryDriveAndNeverThrows()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        harness.Source.HandleFor('T').FailDrive(new IOException("the volume went away"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.Handles.Count);
        foreach (var handle in harness.Source.Handles)
        {
            Assert.AreEqual(1, handle.DisposeCount, $"drive {handle.DriveLetter}");
        }
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterStop()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');

        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.DisposeAsync();

        Assert.IsTrue(handle.ThrowOnSecondDispose);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterDriveFault()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        handle.FailDrive(new IOException("the volume went away"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await handle.Disposed.WaitAsync(HangGuard);

        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        await harness.Index.DisposeAsync();

        Assert.IsTrue(handle.ThrowOnSecondDispose);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_WhenStartReturnsAfterStop()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held, observeToken: false);
        var start = harness.Index.StartWatchingAsync('T', Token);
        await held.Entered.WaitAsync(HangGuard);
        var stop = harness.Index.StopWatchingAsync('T', Token);
        held.Release();
        await stop.WaitAsync(HangGuard);
        await ThrowsAsync<OperationCanceledException>(() => start);

        await harness.Index.DisposeAsync();

        var handle = harness.Source.Handles.Single();
        Assert.IsTrue(handle.ThrowOnSecondDispose);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterIndexDisposal()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');

        await harness.Index.DisposeAsync();

        Assert.IsTrue(handle.ThrowOnSecondDispose);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task WaitForCatchUp_CallerTokenCancelled_ThrowsOperationCanceled()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        using var waitCancellation = new CancellationTokenSource();
        var wait = harness.Index.WaitForCatchUpAsync('T', waitCancellation.Token);

        await waitCancellation.CancelAsync();

        await ThrowsAsync<OperationCanceledException>(() => wait);
        await AssertSlotStillCatchesUpAsync(harness);
    }

    [TestMethod]
    public async Task WaitForCatchUp_DisposalTokenCancelled_ThrowsOperationCanceled()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        // The disposal token alone, without the rest of disposal, so the slot is observable after.
        var disposalCancellation = (CancellationTokenSource)typeof(FileIndex)
            .GetField("_disposalCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(harness.Index)!;
        await disposalCancellation.CancelAsync();

        await ThrowsAsync<OperationCanceledException>(() => wait);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        await harness.Source.HandleFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task PumpFaultSettlesWaiter_ContinuationNotInline()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        harness.Index.PumpFaultSettlementWrapperForTest = settle =>
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
        bool? observedWhileSettling = null;
        var waiter = WaitThenStopOtherDriveAsync(harness.Index);

        harness.Source.HandleFor('T').FailDrive(new IOException("the volume went away"));

        await waiter.WaitAsync(HangGuard);
        Assert.IsFalse(observedWhileSettling, "the waiter's continuation ran on the settling stack");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('U').WatchCatchUp);
        return;

        async Task WaitThenStopOtherDriveAsync(FileIndex index)
        {
            await ThrowsAsync<DriveWatchFaultException>(() => index.WaitForCatchUpAsync('T', Token));
            observedWhileSettling = _settlingPumpFault;
            await index.StopWatchingAsync('U', Token);
        }
    }

    /// <summary>
    ///     Parks the first pump apply on <paramref name="driveLetter" /> inside the apply, before
    ///     its gate, until the returned gate is released. Later applies pass straight through.
    /// </summary>
    static TestGate HoldFirstApply(WatchHarness harness, char driveLetter)
    {
        var gate = harness.TrackGate();
        var held = 0;
        harness.Index.ApplyJournalEntriesEnteredForTest = letter =>
        {
            if (letter == driveLetter && Interlocked.Exchange(ref held, 1) == 0)
            {
                gate.MarkEntered();
                gate.WaitForRelease();
            }
        };
        return gate;
    }

    async Task AssertSlotStillCatchesUpAsync(WatchHarness harness)
    {
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);
        await harness.Source.HandleFor('T').Publish(new DriveCaughtUp());
        await wait.WaitAsync(HangGuard);
    }
}
