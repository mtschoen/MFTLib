using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Automatic recovery (spec 2.6.5): a <see cref="WatchFaultKind.Volume" /> or
///     <see cref="WatchFaultKind.Apply" /> fault queues a rescan of that root, which restarts its
///     watch from the new block. Faults are injected through scripted handles and the producer is
///     the harness's fake, ordered with <see cref="TestGate" />s; no clock is involved. One case
///     records a live-watch loss through <c>JournalIsolation.OverrideJournalWindow</c>, a
///     process-wide seam, hence <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexWatchRecoveryTests
{
    [ThreadStatic] static bool _completingRecoveryTicket;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    static WatchFaultKind[] FaultKinds(WatchHarness harness, char driveLetter) =>
        [.. harness.Faults.Where(fault => fault.Volume == new VolumeKey(driveLetter)).Select(fault => fault.Kind)];

    [TestMethod]
    public async Task RootFault_RecoversByRescan_ReachesCaughtUp_LiveWatchLossSurvives()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(letter => letter == new VolumeKey('T')
            ? new SyntheticJournalWindow(WatchHarness.JournalIdentifier, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9500);

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9500), harness.Source.TargetsFor('T')[^1],
            "the watch restarts from the recovered block's cursor");
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.CatchingUp, root.Watch.State);
        Assert.IsNull(root.Watch.FailureMessage);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, root.Watch.CheckpointLoss?.DetectedDuring,
            "a recovery rescan keeps the live-watch loss that explains it");

        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ApplyFault_Recovers()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, 5000));
        await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task SecondFaultBeforeCaughtUp_RaisesRecovery_NoSecondRescan()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailVolume(new IOException("first fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        var second = new IOException("second fault");

        harness.Source.WatchFor('T').FailVolume(second);
        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');

        Assert.AreSame(second, recoveryFault.Exception.InnerException);
        Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'));
        Assert.IsFalse(harness.IndexFor('T').TryGetRecoveryCompletionForTest(new VolumeKey('T'), out _), "no second recovery is queued");
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreSame(second, failure.InnerException);
    }

    [TestMethod]
    public async Task RecoveryScanFails_RaisesRecovery_RootFaulted()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producerFailure = new IOException("the volume went away");
        harness.FailNextProduction('T', producerFailure);

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.IsInstanceOfType<InvalidOperationException>(recoveryFault.Exception);
        Assert.AreSame(producerFailure, recoveryFault.Exception.InnerException);
        Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'));
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.IsNotNull(root.Watch.FailureMessage);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        Assert.AreEqual(1, harness.RecoveryCount('T'), "no further automatic recovery");
    }

    [TestMethod]
    public async Task ChannelFault_NoRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        var index = harness.Index;

        // Read from this handler's own completion, not from WaitForFaultAsync: the harness's
        // handler runs first and releases that wait, so the test can resume before this one runs.
        var recoveryQueuedWhenRaised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Channel && fault.Volume == new VolumeKey('T'))
            {
                recoveryQueuedWhenRaised.TrySetResult(index.TryGetRecoveryCompletionForTest(new VolumeKey('T'), out _));
            }
        };

        harness.Source.WatchFor('T').LoseChannel(new IOException("the broker died"));
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');

        Assert.IsFalse(await recoveryQueuedWhenRaised.Task.WaitAsync(HangGuard, TestContext.CancellationToken), "a channel fault queues no recovery");
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
        Assert.AreEqual("the broker died", harness.RootFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task StopDuringRecovery_CommitsWithoutRestart()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var original = harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock;
        var producedBefore = harness.ProductionCount('T');
        var held = harness.HoldNextProduction('T');

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        held.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreNotSame(original, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock, "the recovery committed its block");
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the stopped watch is not restarted");
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task DisposeDuringRecovery_CancelsIt()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var held = harness.HoldNextProduction('T');
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var recovery = harness.RecoveryCompletion('T');
        Assert.IsFalse(recovery.IsCompleted);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsTrue(recovery.IsCompleted, "disposal cancels the recovery and waits for it");
        await recovery;
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task ConsumerRescanAfterRecoveryFault_RestoresWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.FailNextProduction('T', new IOException("the volume went away"));
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.CatchingUp, root.Watch.State);
        Assert.IsNull(root.Watch.FailureMessage);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task RecoveringState_ReportedInRootStatus()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var held = harness.HoldNextProduction('T');
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);
        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').WaitForCatchUpAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        held.Release();
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task RecoveringPublishedBeforeWatchFaultedRaised()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var index = harness.Index;
        WatchState? seenByHandler = null;
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Volume)
            {
                seenByHandler = index.Status.Watch.State;
            }
        };

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(WatchState.Recovering, seenByHandler);
    }

    [TestMethod]
    public async Task QueuedRecovery_SupersededByManualRescan_DoesNotScanAgain()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var beforeGate = HoldRecoveryBeforeItsGate(harness, 'T');
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await beforeGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var producedBefore = harness.ProductionCount('T');

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        beforeGate.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the superseded recovery does not scan");
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task QueuedRecovery_AfterStop_IsDropped()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var beforeGate = HoldRecoveryBeforeItsGate(harness, 'T');
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await beforeGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
        beforeGate.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task QueuedRecovery_SupersededByConsumerStart_DoesNotScan()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var beforeGate = HoldRecoveryBeforeItsGate(harness, 'T');
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await beforeGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(harness.IndexFor('T').TryGetRecoveryCompletionForTest(new VolumeKey('T'), out _), "the start clears the ticket");
        beforeGate.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task ObsoleteRecoveryFailure_DoesNotFaultNewerWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var held = harness.HoldNextProduction('T');
        harness.FailNextProduction('T', new IOException("the volume went away"));
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        var newerStart = harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.IsFalse(newerStart.IsCompleted, "the start waits for the recovery's lifecycle gate");

        held.Release();
        await newerStart.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.CatchingUp, root.Watch.State);
        Assert.IsNull(root.Watch.FailureMessage);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task TwoRootsFaultTogether_RecoverConcurrently()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var heldT = harness.HoldNextProduction('T');
        var heldU = harness.HoldNextProduction('U');

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        harness.Source.WatchFor('U').FailVolume(new IOException("U's journal wrapped"));
        await Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);
        heldT.Release();
        heldU.Release();
        await harness.WaitForRecoveryAsync('T');
        await harness.WaitForRecoveryAsync('U');

        foreach (var driveLetter in new[] { 'T', 'U' })
        {
            Assert.AreEqual(WatchState.CatchingUp, harness.RootFor(driveLetter).Watch.State);
            Assert.HasCount(2, harness.Source.TargetsFor(driveLetter));
        }
    }

    [TestMethod]
    public async Task RecoveryTicketCompletion_ContinuationNotInline()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var index = harness.Index;
        index.Hooks.RecoveryCompletionWrapper = complete =>
        {
            _completingRecoveryTicket = true;
            try
            {
                complete();
            }
            finally
            {
                _completingRecoveryTicket = false;
            }
        };
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Volume && index.TryGetRecoveryCompletionForTest(new VolumeKey('T'), out var completion))
            {
                _ = completion.ContinueWith(_ => observed.TrySetResult(_completingRecoveryTicket),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        };

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));

        Assert.IsFalse(await observed.Task.WaitAsync(HangGuard, TestContext.CancellationToken),
            "the ticket's continuation ran on the stack that completed it");
    }

    /// <summary>
    ///     Parks the root's next recovery before it takes the root's lifecycle gate, until the
    ///     returned gate is released.
    /// </summary>
    static TestGate HoldRecoveryBeforeItsGate(WatchHarness harness, char driveLetter)
    {
        var gate = harness.TrackGate();
        var held = 0;
        harness.Index.Hooks.RecoveryBeforeLifecycleGate = async (letter, cancellationToken) =>
        {
            if (letter == new VolumeKey(driveLetter) && Interlocked.Exchange(ref held, 1) == 0)
            {
                gate.MarkEntered();
                await gate.WaitForReleaseAsync(cancellationToken);
            }
        };
        return gate;
    }
}
