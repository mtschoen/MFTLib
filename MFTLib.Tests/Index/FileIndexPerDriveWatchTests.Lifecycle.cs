using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

public partial class FileIndexPerDriveWatchTests
{
    [ThreadStatic] static bool _settlingPumpFault;

    [TestMethod]
    public async Task RestartAfterStop_AwaitsOldDrain_BeforePublishing()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var oldHandle = harness.Source.WatchFor('T');
        var applying = harness.HoldFirstApply('T');
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
        var applying = harness.HoldFirstApply('T');
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
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
        Assert.AreEqual(WatchHarness.NextUsn, harness.Source.TargetsFor('T')[1].NextUsn,
            "the successor was armed from the cursor the dropped batch would have moved");
    }

    [TestMethod]
    public async Task OldInstanceFault_AfterRestart_DoesNotTouchNewInstance()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var oldHandle = harness.Source.WatchFor('T');
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('T', Token);

        oldHandle.LoseChannel(new IOException("the old pipe broke late"));
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "fresh.txt"));

        Assert.AreEqual(0, harness.Faults.Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task RetiredInstanceFault_IsNotRecorded()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.WatchFor('T');
        handle.FailOnCancellation(new IOException("the pipe closed under the pending read"));

        await harness.Index.StopWatchingAsync('T', Token);

        Assert.AreEqual(0, harness.Faults.Count, "a stopped watch's last error belongs to no current watch");
        var drive = harness.DriveFor('T');
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.Watch.CatchUpState);
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task Rescan_TokenCancelledAfterCommit_DrainsAndStartsTheReplacement()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var applying = harness.HoldFirstApply('T');
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard);
        using var rescanCancellation = new CancellationTokenSource();
        var rescan = harness.Index.RescanAsync('T', rescanCancellation.Token);

        await rescanCancellation.CancelAsync();
        applying.Release();

        await rescan.WaitAsync(HangGuard);
        var starts = harness.Source.TargetsFor('T');
        Assert.AreEqual(2, starts.Count, "cancellation after commit does not abandon the replacement watch");
        Assert.AreEqual(new IndexWatchTarget('T', WatchHarness.JournalIdentifier, WatchHarness.NextUsn), starts[1]);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
    }

    [TestMethod]
    public async Task TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        await harness.Index.StartWatchingAsync('V', Token);
        var draining = harness.HoldFirstApply('U');
        _ = harness.Source.WatchFor('U').Queue(WatchHarness.Batch(10, "u.txt"));
        await draining.Entered.WaitAsync(HangGuard);
        using var waitCancellation = new CancellationTokenSource();
        using var stopCancellation = new CancellationTokenSource();
        var waiter = ObserveAsync(harness.Index.WaitForCatchUpAsync('V', waitCancellation.Token));
        var stopper = ObserveAsync(harness.Index.StopWatchingAsync('U', stopCancellation.Token));
        harness.Index.Changed += CancelWhileFlagged("t.txt", waitCancellation, stopCancellation);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "t.txt"));

        var observed = await Task.WhenAll(waiter, stopper).WaitAsync(HangGuard);
        Assert.IsFalse(observed[0], "the catch-up wait's continuation ran on the cancelling handler's stack");
        Assert.IsFalse(observed[1], "the stop's continuation ran on the cancelling handler's stack");
        draining.Release();
        return;

        static async Task<bool> ObserveAsync(Task operation)
        {
            await ThrowsAsync<OperationCanceledException>(() => operation);
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
        var start = harness.Index.StartWatchingAsync('T', Token);
        await held.Entered.WaitAsync(HangGuard);
        var productionsBefore = harness.ProductionCount('T');

        var rescan = harness.Index.RescanAsync('T', rescanCancellation.Token);
        await rescanCancellation.CancelAsync();

        // The rescan was waiting for the starting watch's lifecycle gate and had touched nothing, so
        // it scans nothing and records no failure against a drive the start is about to watch.
        await ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard));
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(productionsBefore, harness.ProductionCount('T'));

        held.Release();
        await start.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhileARescanProduces_WatchesOnceFromTheFreshCursor()
    {
        using var harness = new WatchHarness();
        var producing = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        var rescan = harness.Index.RescanAsync('T', Token);
        await producing.Entered.WaitAsync(HangGuard);

        var start = harness.Index.StartWatchingAsync('T', Token);
        Assert.IsFalse(start.IsCompleted, "the start waits for the rescan's lifecycle gate");
        Assert.AreEqual(0, harness.Source.Starts.Count);
        producing.Release();
        await rescan.WaitAsync(HangGuard);
        await start.WaitAsync(HangGuard);

        CollectionAssert.AreEqual(new[] { new IndexWatchTarget('T', 13, 9000) }, harness.Source.Targets.ToArray());
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        await harness.Index.StopWatchingAsync('T', Token);
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
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
    }

    [TestMethod]
    public async Task Rescan_RestartsWatchFromFreshCursor()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var firstHandle = harness.Source.WatchFor('T');
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);

        await harness.Index.RescanAsync('T', Token);

        var starts = harness.Source.TargetsFor('T');
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), starts[1]);
        Assert.AreEqual(1, firstHandle.DisposeCount);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
    }

    [TestMethod]
    public async Task Dispose_StopsEveryDriveAndNeverThrows()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.Watches.Count);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount, $"drive {handle.DriveLetter}");
        }
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterStop()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.WatchFor('T');

        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterDriveFault()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.WatchFor('T');
        handle.FailDrive(new IOException("the volume went away"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await handle.Disposed.WaitAsync(HangGuard);

        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task HandleDisposedExactlyOnce_AfterIndexDisposal()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.WatchFor('T');

        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, handle.DisposeCount);
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
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').Watch.CatchUpState);
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
        var waiter = WaitThenStopOtherDriveAsync(harness.Index);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));

        Assert.IsFalse(await waiter.WaitAsync(HangGuard), "the waiter's continuation ran on the settling stack");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('U').Watch.CatchUpState);
        return;

        async Task<bool> WaitThenStopOtherDriveAsync(FileIndex index)
        {
            await ThrowsAsync<DriveWatchFaultException>(() => index.WaitForCatchUpAsync('T', Token));
            var observedWhileSettling = _settlingPumpFault;
            await index.StopWatchingAsync('U', Token);
            return observedWhileSettling;
        }
    }

}
