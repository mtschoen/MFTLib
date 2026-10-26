// Over 500 lines on purpose: one class pins one guard, and every case is a self-contained handler scenario.
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A <see cref="FileIndex.Changed" /> or <see cref="FileIndex.WatchFaulted" /> handler runs on a
///     drive's pump, or on the scan operation that raised a lost catch-up, so a lifecycle call it
///     blocks on can wait for a pump that is itself blocked in a handler (spec 2.6.8). Every such
///     call is rejected at entry whichever drive it names. Each handler here catches the outcome
///     of its blocking call inside a bounded wait and reports it through a signal, so a regression
///     shows as a timeout and never as a hung suite. Ordering comes from <see cref="TestGate" />
///     signals; no clock, no sleeps.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCallbackReentrancyTests
{
    [ThreadStatic] static bool _insideHandlerStack;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task ChangedHandlerOnX_CallsStopOnY_ThrowsImmediately_YKeepsWatching()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var outcome = NewSignal<Exception?>();
        OnChanged(harness, "t.txt", () => outcome.TrySetResult(BlockOn(() => index.StopWatchingAsync(['U'], CancellationToken.None))));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        var consumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));
        var failure = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        await consumed.WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertRejected(failure, "StopWatchingAsync");
        Assert.AreEqual(0, harness.Source.WatchFor('U').DisposeCount, "Y's watch was not stopped");
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 300));
        Assert.Contains(change => change.Entry.Name == "u.txt", harness.Changes, "Y still applies batches");
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('U').Watch.State);
    }

    [TestMethod]
    public async Task ChangedHandlerOnX_CallsStopOnItsOwnDrive_Throws()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var outcome = NewSignal<Exception?>();
        OnChanged(harness, "t.txt", () => outcome.TrySetResult(BlockOn(() => index.StopWatchingAsync(['T'], CancellationToken.None))));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        var consumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));
        var failure = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        await consumed.WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertRejected(failure, "StopWatchingAsync");
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(10, "t2.txt", nextUsn: 400));
        Assert.Contains(change => change.Entry.Name == "t2.txt", harness.Changes, "X's pump is still running");
    }

    [TestMethod]
    public async Task TwoHandlerCycle_EachStopsTheOtherDrive_BothFailAtOnce()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var tGate = harness.TrackGate();
        var uGate = harness.TrackGate();
        var tOutcome = NewSignal<Exception?>();
        var uOutcome = NewSignal<Exception?>();
        index.Changed += change =>
        {
            if (change.Entry.Name == "t.txt")
            {
                tGate.MarkEntered();
                tGate.WaitForRelease();
                tOutcome.TrySetResult(BlockOn(() => index.StopWatchingAsync(['U'], CancellationToken.None)));
            }
            else if (change.Entry.Name == "u.txt")
            {
                uGate.MarkEntered();
                uGate.WaitForRelease();
                uOutcome.TrySetResult(BlockOn(() => index.StopWatchingAsync(['T'], CancellationToken.None)));
            }
        };
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);
        var tConsumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));
        var uConsumed = harness.Source.WatchFor('U').Queue(WatchHarness.Batch(10, "u.txt", nextUsn: 300));
        await tGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await uGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        tGate.Release();
        uGate.Release();

        AssertRejected(await tOutcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "StopWatchingAsync");
        AssertRejected(await uOutcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "StopWatchingAsync");
        await tConsumed.WaitAsync(HangGuard, TestContext.CancellationToken);
        await uConsumed.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(11, "t2.txt", nextUsn: 400));
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(12, "u2.txt", nextUsn: 400));
        Assert.Contains(change => change.Entry.Name == "t2.txt", harness.Changes, "both pumps continue");
        Assert.Contains(change => change.Entry.Name == "u2.txt", harness.Changes, "both pumps continue");
    }

    [TestMethod]
    public async Task WatchFaultedHandler_CallsRescanOfAnotherDrive_Throws()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        var index = harness.Index;
        var outcome = NewSignal<Exception?>();
        OnDriveFault(harness, () => outcome.TrySetResult(BlockOn(() => index.RescanAsync(['U'], CancellationToken.None))));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "RescanAsync");
    }

    [TestMethod]
    public async Task WatchFaultedHandler_CallsStartOfAnotherDrive_Throws()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        var index = harness.Index;
        var outcome = NewSignal<Exception?>();
        OnDriveFault(harness, () => outcome.TrySetResult(BlockOn(() => index.StartWatchingAsync(['U'], CancellationToken.None))));
        await harness.Index.StartWatchingAsync(['T'], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "StartWatchingAsync");
        Assert.IsEmpty(harness.Source.TargetsFor('U'), "the rejected start never reached the source");
    }

    [TestMethod]
    public async Task ChangedHandler_CallsDispose_Throws()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var outcome = NewSignal<Exception?>();
        OnChanged(harness, "t.txt", () => outcome.TrySetResult(BlockOn(() => index.DisposeAsync().AsTask())));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        var consumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "DisposeAsync");
        await consumed.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 300));
        Assert.Contains(change => change.Entry.Name == "u.txt", harness.Changes, "the index was not disposed");
    }

    [TestMethod]
    public async Task ChangedHandler_CallsUnsettledWaitForCatchUp_Throws()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var outcome = NewSignal<Exception?>();
        OnChanged(harness, "t.txt", () => outcome.TrySetResult(BlockOn(() => index.WaitForCatchUpAsync(['U'], CancellationToken.None))));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "WaitForCatchUpAsync");
    }

    [TestMethod]
    public async Task ChangedHandler_CallsSettledWaitForCatchUp_ReturnsItsResult()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var outcome = NewSignal<(bool Settled, Exception? SingleFailure, Exception? BatchedFailure)>();
        OnChanged(harness, "t.txt", () =>
        {
            var single = index.WaitForCatchUpAsync(['T'], CancellationToken.None);
            var batched = index.WaitForCatchUpAsync(['T'], CancellationToken.None);
            outcome.TrySetResult((single.IsCompleted, BlockOn(() => single), BlockOn(() => batched)));
        });
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await index.WaitForCatchUpAsync(['T'], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        var result = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsTrue(result.Settled, "the wait was already settled when the handler asked");
        Assert.IsNull(result.SingleFailure);
        Assert.IsNull(result.BatchedFailure);
    }

    [TestMethod]
    public async Task ChangedHandler_EveryLifecycleEntryPoint_IsRejectedNamingItself()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var token = CancellationToken.None;
        var calls = new (string Operation, Func<Task> Call)[]
        {
            ("StartWatchingAsync", () => index.StartWatchingAsync(['U'], token)),
            ("StartWatchingAsync", () => index.StartWatchingAsync(['U'], token)),
            ("StartWatchingAsync", () => index.StartWatchingAsync(index.Drives.Select(drive => drive.Drive),token)),
            ("StopWatchingAsync", () => index.StopWatchingAsync(['U'], token)),
            ("StopWatchingAsync", () => index.StopWatchingAsync(['U'], token)),
            ("StopWatchingAsync", () => index.StopWatchingAsync(index.Drives.Select(drive => drive.Drive),token)),
            ("RescanAsync", () => index.RescanAsync(['U'], token)),
            ("RescanAsync", () => index.RescanAsync(['U'], token)),
            ("WaitForCatchUpAsync", () => index.WaitForCatchUpAsync(['U'], token)),
            ("WaitForCatchUpAsync", () => index.WaitForCatchUpAsync(['U'], token)),
            ("WaitForCatchUpAsync", () => index.WaitForCatchUpAsync(index.Drives.Select(drive => drive.Drive),token)),
            ("DisposeAsync", () => index.DisposeAsync().AsTask())
        };
        var outcome = NewSignal<Exception?[]>();
        OnChanged(harness, "t.txt", () => outcome.TrySetResult([.. calls.Select(call => BlockOn(call.Call))]));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        var failures = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        for (var position = 0; position < calls.Length; position++)
        {
            AssertRejected(failures[position], calls[position].Operation);
        }
    }

    [TestMethod]
    public async Task CatchUpLostHandler_CallsRescanOfAnotherDrive_Throws()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        harness.ScriptScans('T', new WatchHarnessScan(CatchUpLoss: CatchUpLossTestSupport.StandardCatchUpLoss('T')));
        var outcome = NewSignal<Exception?>();
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.CatchUpLost)
            {
                outcome.TrySetResult(BlockOn(() => index.RescanAsync(['U'], CancellationToken.None)));
            }
        };

        await index.RescanAsync(['T'], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "RescanAsync");
    }

    [TestMethod]
    public async Task MarkerSurvivesAwaitInsideHandler_StopStillRejected()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var completedGate = harness.TrackGate();
        completedGate.MarkEntered();
        var outcome = NewSignal<Exception?>();
        OnChanged(harness, "t.txt", () => outcome.TrySetResult(BlockOn(async () =>
        {
            await Task.Run(() => completedGate.Entered, TestContext.CancellationToken).ConfigureAwait(false);
            await index.StopWatchingAsync(['U'], CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken).ConfigureAwait(false);
        })));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "StopWatchingAsync");
        Assert.AreEqual(0, harness.Source.WatchFor('U').DisposeCount);
    }

    [TestMethod]
    public async Task QueuedWorkAfterHandlerReturns_IsAllowed()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var afterHandler = harness.TrackGate();
        var queuedStop = NewSignal<Task>();
        OnChanged(harness, "t.txt", () => queuedStop.TrySetResult(Task.Run(async () =>
        {
            await afterHandler.WaitForReleaseAsync(CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken).ConfigureAwait(false);
            await index.StopWatchingAsync(['T'], CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken).ConfigureAwait(false);
        }, TestContext.CancellationToken)));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        var consumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));
        await consumed.WaitAsync(HangGuard, TestContext.CancellationToken);
        afterHandler.Release();
        var stop = await queuedStop.Task.WaitAsync(HangGuard, TestContext.CancellationToken);

        await stop.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(1, harness.Source.WatchFor('T').DisposeCount, "X drained");
    }

    [TestMethod]
    public async Task RecoveryQueuedWhileAHandlerRan_StillRestartsTheWatch()
    {
        using var harness = new WatchHarness('T', 'U');
        var handlerRan = NewSignal<bool>();
        OnDriveFault(harness, () => handlerRan.TrySetResult(true));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        await handlerRan.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.WaitForRecoveryAsync('T');

        Assert.HasCount(2, harness.Source.TargetsFor('T'), "the recovery restarted the watch");
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task HandlerMayQueryAndGrow()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        await using var broker = new InProcessBroker(new JournalBrokerHost(
            new ScriptedVolumeAccess
            {
                CursorQuery = _ => new UsnJournalCursor(7, 1000),
                RecordBatches = (_, _, _, _, _, _) => [],
                JournalRead = (_, since, _) => ([], since),
                VolumeInformation = _ => new NtfsVolumeInformation(1024 * 1000, 1024),
                JournalGrow = (_, maximumSize, allocationDelta) =>
                    new UsnJournalSettings { MaximumSize = maximumSize * 2, AllocationDelta = allocationDelta }
            },
            processorCount: 4));
        var process = broker.Process;
        var outcome = NewSignal<(int DriveCount, long GrownSize, Exception? Failure)>();
        OnChanged(harness, "t.txt", () =>
        {
            try
            {
                _ = index.Search(new SearchQuery("t"), TestContext.CancellationToken);
                var grown = process.GrowUsnJournalAsync('E', new UsnJournalSettings { MaximumSize = 65536, AllocationDelta = 4096 }, CancellationToken.None)
                    .WaitAsync(HangGuard, TestContext.CancellationToken).GetAwaiter().GetResult();
                outcome.TrySetResult((index.Drives.Count, grown.MaximumSize, null));
            }
            catch (Exception thrown)
            {
                outcome.TrySetResult((0, 0, thrown));
            }
        });
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        var result = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(2, result.DriveCount);
        Assert.AreEqual(131072L, result.GrownSize);
    }

    [TestMethod]
    public async Task HandlerCancelsWaitersToken_ContinuationNotInline()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        var index = harness.Index;
        using var waitCancellation = new CancellationTokenSource();
        var observedInline = NewSignal<bool>();
        OnDriveFault(harness, CancelFromHandlerStack(waitCancellation));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);
        var wait = index.WaitForCatchUpAsync(['U'], waitCancellation.Token);
        var awaiter = ObserveCancellationAsync(wait, observedInline, () => index.DisposeAsync().AsTask());

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));

        Assert.IsFalse(await observedInline.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "the continuation ran on the handler's stack");
        await awaiter.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task StopCancellationSettlesOffTheHandlersStack()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        using var stopCancellation = new CancellationTokenSource();
        var uGate = harness.TrackGate();
        var observedInline = NewSignal<bool>();
        var cancelFromHandler = CancelFromHandlerStack(stopCancellation);
        index.Changed += change =>
        {
            if (change.Entry.Name == "u.txt")
            {
                uGate.MarkEntered();
                uGate.WaitForRelease();
            }
            else if (change.Entry.Name == "t.txt")
            {
                cancelFromHandler();
            }
        };
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);
        var uConsumed = harness.Source.WatchFor('U').Queue(WatchHarness.Batch(10, "u.txt", nextUsn: 300));
        await uGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        harness.SetNextProducedCursor('U', WatchHarness.JournalIdentifier, nextUsn: 300);
        var stop = index.StopWatchingAsync(['U'], stopCancellation.Token);
        Assert.IsFalse(stop.IsCompleted, "the stop waits for U's pump, which is inside its handler");
        var token = Token;
        var awaiter = ObserveCancellationAsync(stop, observedInline, () => index.RescanAsync(['U'], token));

        var tConsumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        Assert.IsFalse(await observedInline.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "the caller's catch ran on the handler's stack");
        await tConsumed.WaitAsync(HangGuard, TestContext.CancellationToken);
        uGate.Release();
        await awaiter.WaitAsync(HangGuard, TestContext.CancellationToken);
        await uConsumed.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WatchFaultedSubscriberA_QueuedWorkRunsWhileSubscriberBStillRuns_IsAllowed()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        var index = harness.Index;
        var subscriberBGate = harness.TrackGate();
        var outcome = NewSignal<Exception?>();
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Drive && fault.Drive == 'T')
            {
                _ = Task.Run(async () =>
                {
                    await subscriberBGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken).ConfigureAwait(false);
                    outcome.TrySetResult(BlockOn(() => index.StopWatchingAsync(['U'], CancellationToken.None)));
                }, TestContext.CancellationToken);
            }
        };
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Drive && fault.Drive == 'T')
            {
                subscriberBGate.MarkEntered();
                subscriberBGate.WaitForRelease();
            }
        };
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        var failure = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        subscriberBGate.Release();

        Assert.IsNull(failure, "work subscriber A queued ran after A returned, so it is allowed while B runs");
        Assert.AreEqual(1, harness.Source.WatchFor('U').DisposeCount);
    }

    [TestMethod]
    public async Task ChangedHandler_CallsWaitWhileDisposalBeginsDuringDriveEnumeration_GetsTheGuardException()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var materializing = harness.TrackGate();
        var outcome = NewSignal<Exception?>();
        Task? disposal = null;

        IEnumerable<DriveKey> ResolveDrives()
        {
            materializing.MarkEntered();
            materializing.WaitForRelease();
            foreach (var drive in index.Drives)
            {
                yield return drive.Drive;
            }
        }

        OnChanged(harness, "t.txt", () => outcome.TrySetResult(BlockOn(
            () => index.WaitForCatchUpAsync(ResolveDrives(), CancellationToken.None))));
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);
        var consumed = harness.Source.WatchFor('T').Queue(
            WatchHarness.Batch(9, "t.txt", nextUsn: 300));
        try
        {
            await materializing.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            disposal = index.DisposeAsync().AsTask();
            Assert.ThrowsExactly<ObjectDisposedException>(() => index.Drives);
            Assert.IsFalse(disposal.IsCompleted, "disposal still waits for the held handler's pump");
            materializing.Release();
            AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "WaitForCatchUpAsync");
        }
        finally
        {
            materializing.Release();
            await consumed.WaitAsync(HangGuard, TestContext.CancellationToken);
            await (disposal ?? index.DisposeAsync().AsTask()).WaitAsync(HangGuard, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    public async Task NestedDeliveryOnAnotherIndex_StillRejectsACallOnTheOuterIndex()
    {
        using var outer = new WatchHarness('T', 'U');
        using var inner = new WatchHarness('T', 'U');
        var outerIndex = outer.Index;
        var outcome = NewSignal<Exception?>();
        inner.Index.Changed += change =>
        {
            if (change.Entry.Name == "inner.txt")
            {
                outcome.TrySetResult(BlockOn(() => outerIndex.StopWatchingAsync(['U'], CancellationToken.None)));
            }
        };
        var innerIndex = inner.Index;
        OnChanged(outer, "t.txt", () =>
        {
            if (BlockOn(() => innerIndex.ApplyJournalEntriesAsync('T', [WatchHarness.Create(20, "inner.txt")],
                    WatchHarness.JournalIdentifier, 500)) is { } failure)
            {
                outcome.TrySetException(failure);
            }
        });
        await StartBothAsync(outer).WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = outer.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));

        AssertRejected(await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "StopWatchingAsync");
        Assert.AreEqual(0, outer.Source.WatchFor('U').DisposeCount);
    }

    [TestMethod]
    public async Task ChangedHandler_CallsLifecycleMethodsWhileTheIndexIsDisposing_GetsTheGuardException()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        var gate = harness.TrackGate();
        var outcome = NewSignal<Exception?[]>();
        OnChanged(harness, "t.txt", () =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
            outcome.TrySetResult([
                BlockOn(() => index.WaitForCatchUpAsync(['T', 'U'], CancellationToken.None)),
                BlockOn(() => index.StartWatchingAsync(['T', 'U'], CancellationToken.None)),
                BlockOn(() => index.StopWatchingAsync(['T', 'U'], CancellationToken.None))
            ]);
        });
        await StartBothAsync(harness).WaitAsync(HangGuard, TestContext.CancellationToken);
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt", nextUsn: 300));
        await gate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var disposal = index.DisposeAsync().AsTask();

        gate.Release();

        var failures = await outcome.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertRejected(failures[0], "WaitForCatchUpAsync");
        AssertRejected(failures[1], "StartWatchingAsync");
        AssertRejected(failures[2], "StopWatchingAsync");
        await disposal.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    /// <summary>
    ///     Awaits <paramref name="pending" /> from the calling thread, so the continuation is registered
    ///     before anything cancels it, records whether the cancellation's continuation ran on the
    ///     stack of a handler that set <see cref="_insideHandlerStack" />, then runs <paramref name="afterwards" />
    ///     unless it did, so a failing run reports the assertion and not a teardown race.
    /// </summary>
    static async Task ObserveCancellationAsync(Task pending, TaskCompletionSource<bool> observedInline,
        Func<Task> afterwards)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var inline = _insideHandlerStack;
            observedInline.TrySetResult(inline);
            if (inline)
            {
                return;
            }
        }

        await afterwards().ConfigureAwait(false);
    }

    /// <summary>
    ///     A handler body that cancels <paramref name="source" /> synchronously while
    ///     <see cref="_insideHandlerStack" /> is set on the calling thread.
    /// </summary>
    static Action CancelFromHandlerStack(CancellationTokenSource source) => () =>
    {
        _insideHandlerStack = true;
        try
        {
            source.Cancel();
        }
        finally
        {
            _insideHandlerStack = false;
        }
    };

    static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static async Task StartBothAsync(WatchHarness harness)
    {
        await harness.Index.StartWatchingAsync(['T'], CancellationToken.None).WaitAsync(HangGuard);
        await harness.Index.StartWatchingAsync(['U'], CancellationToken.None).WaitAsync(HangGuard);
    }

    static void OnChanged(WatchHarness harness, string fileName, Action handler)
    {
        harness.Index.Changed += change =>
        {
            if (change.Entry.Name == fileName)
            {
                handler();
            }
        };
    }

    static void OnDriveFault(WatchHarness harness, Action handler)
    {
        harness.Index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Drive && fault.Drive == 'T')
            {
                handler();
            }
        };
    }

    /// <summary>
    ///     Runs the call the way a blocking handler would: waits for its task, bounded, and reports
    ///     the failure it ended with, a <see cref="TimeoutException" /> when it never ended, and
    ///     null when it completed.
    /// </summary>
    static Exception? BlockOn(Func<Task> call)
    {
        try
        {
            return call().Wait(HangGuard) ? null : new TimeoutException("The call did not settle.");
        }
        catch (AggregateException failure)
        {
            return failure.InnerException;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    static void AssertRejected(Exception? failure, string operation)
    {
        Assert.IsInstanceOfType<InvalidOperationException>(failure, $"{operation} was not rejected: {failure}");
        Assert.AreEqual(
            $"FileIndex.{operation} was called from inside a Changed, WatchFaulted or WatchStateChanged handler; it " +
            "can wait for a watch pump that is blocked in a handler. Queue the call to run after the handler " +
            "returns, for example with Task.Run.",
            failure.Message);
    }

}
