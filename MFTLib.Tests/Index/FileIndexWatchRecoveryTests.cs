using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Automatic recovery (spec 2.6.5): a <see cref="WatchFaultKind.Drive" /> or
///     <see cref="WatchFaultKind.Apply" /> fault queues a rescan of that drive, which restarts its
///     watch from the new block. Faults are injected through scripted handles and the producer is
///     the harness's fake, ordered with <see cref="TestGate" />s; no clock is involved. One case
///     records a live-watch loss through <c>JournalCheckpointCheck.OverrideJournalForTest</c>, a
///     process-wide seam, hence <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexWatchRecoveryTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    [ThreadStatic] static bool _completingRecoveryTicket;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static WatchFaultKind[] FaultKinds(WatchHarness harness, char driveLetter) =>
        harness.Faults.Where(fault => fault.DriveLetter == driveLetter).Select(fault => fault.Kind).ToArray();

    [TestMethod]
    public async Task DriveFault_RecoversByRescan_ReachesCaughtUp_LiveWatchLossSurvives()
    {
        using var harness = new WatchHarness('T', 'U');
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(letter => letter == 'T'
            ? new JournalWindow(WatchHarness.JournalId, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);
        await harness.Index.StartWatchingAsync('T', Token);
        var producedBefore = harness.ProductionCount('T');
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9500);

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9500), harness.Source.TargetsFor('T')[^1],
            "the watch restarts from the recovered block's cursor");
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.CatchingUp, drive.Watch.CatchUpState);
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, drive.Watch.CheckpointLoss?.DetectedDuring,
            "a recovery rescan keeps the live-watch loss that explains it");

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').Watch.CatchUpState);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task ApplyFault_Recovers()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var producedBefore = harness.ProductionCount('T');

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalId, 5000));
        await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task SecondFaultBeforeCaughtUp_RaisesRecovery_NoSecondRescan()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("first fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        var second = new IOException("second fault");

        harness.Source.WatchFor('T').FailDrive(second);
        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');

        Assert.AreSame(second, recoveryFault.Exception.InnerException);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive, WatchFaultKind.Recovery }, FaultKinds(harness, 'T'));
        Assert.IsFalse(harness.Index.TryGetRecoveryCompletionForTest('T', out _), "no second recovery is queued");
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').Watch.CatchUpState);
        var thrown = await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreSame(second, thrown.InnerException);
    }

    [TestMethod]
    public async Task RecoveryScanFails_RaisesRecovery_DriveFaulted()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var producerFailure = new IOException("the volume went away");
        harness.FailNextProduction('T', producerFailure);

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.IsInstanceOfType<InvalidOperationException>(recoveryFault.Exception);
        Assert.AreSame(producerFailure, recoveryFault.Exception.InnerException);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive, WatchFaultKind.Recovery }, FaultKinds(harness, 'T'));
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.Watch.CatchUpState);
        Assert.IsNotNull(drive.Watch.FailureMessage);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(1, harness.RecoveryCount('T'), "no further automatic recovery");
    }

    [TestMethod]
    public async Task ChannelFault_NoRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var producedBefore = harness.ProductionCount('T');
        var index = harness.Index;

        // Read from this handler's own completion, not from WaitForFaultAsync: the harness's
        // handler runs first and releases that wait, so the test can resume before this one runs.
        var recoveryQueuedWhenRaised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Channel && fault.DriveLetter == 'T')
            {
                recoveryQueuedWhenRaised.TrySetResult(index.TryGetRecoveryCompletionForTest('T', out _));
            }
        };

        harness.Source.WatchFor('T').LoseChannel(new IOException("the broker died"));
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');

        Assert.IsFalse(await recoveryQueuedWhenRaised.Task.WaitAsync(HangGuard), "a channel fault queues no recovery");
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').Watch.CatchUpState);
        Assert.AreEqual("the broker died", harness.DriveFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task StopDuringRecovery_CommitsWithoutRestart()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var original = harness.Index.Root('T').DriveBlock;
        var producedBefore = harness.ProductionCount('T');
        var held = harness.HoldNextProduction('T');

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard);
        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        held.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreNotSame(original, harness.Index.Root('T').DriveBlock, "the recovery committed its block");
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the stopped watch is not restarted");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task DisposeDuringRecovery_CancelsIt()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var held = harness.HoldNextProduction('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard);
        var recovery = harness.RecoveryCompletion('T');
        Assert.IsFalse(recovery.IsCompleted);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.IsTrue(recovery.IsCompleted, "disposal cancels the recovery and waits for it");
        await recovery;
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task ConsumerRescanAfterRecoveryFault_RestoresWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        harness.FailNextProduction('T', new IOException("the volume went away"));
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.CatchingUp, drive.Watch.CatchUpState);
        Assert.IsNull(drive.Watch.FailureMessage);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').Watch.CatchUpState);
    }

    [TestMethod]
    public async Task RecoveringState_ReportedInDriveStatus()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var held = harness.HoldNextProduction('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard);

        Assert.AreEqual(WatchCatchUpState.Recovering, harness.DriveFor('T').Watch.CatchUpState);
        await ThrowsAsync<DriveWatchFaultException>(
            () => harness.Index.WaitForCatchUpAsync('T', Token).WaitAsync(HangGuard));

        held.Release();
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
    }

    [TestMethod]
    public async Task RecoveringPublishedBeforeWatchFaultedRaised()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var index = harness.Index;
        WatchCatchUpState? seenByHandler = null;
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Drive)
            {
                seenByHandler = index.Drives.Single(drive => drive.DriveLetter == 'T').Watch.CatchUpState;
            }
        };

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(WatchCatchUpState.Recovering, seenByHandler);
    }

    [TestMethod]
    public async Task QueuedRecovery_SupersededByManualRescan_DoesNotScanAgain()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var beforeGate = HoldRecoveryBeforeItsGate(harness, 'T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await beforeGate.Entered.WaitAsync(HangGuard);
        var producedBefore = harness.ProductionCount('T');

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        beforeGate.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the superseded recovery does not scan");
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task QueuedRecovery_AfterStop_IsDropped()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var beforeGate = HoldRecoveryBeforeItsGate(harness, 'T');
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await beforeGate.Entered.WaitAsync(HangGuard);

        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
        beforeGate.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
    }

    [TestMethod]
    public async Task QueuedRecovery_SupersededByConsumerStart_DoesNotScan()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var beforeGate = HoldRecoveryBeforeItsGate(harness, 'T');
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await beforeGate.Entered.WaitAsync(HangGuard);

        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Assert.IsFalse(harness.Index.TryGetRecoveryCompletionForTest('T', out _), "the start clears the ticket");
        beforeGate.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
    }

    [TestMethod]
    public async Task ObsoleteRecoveryFailure_DoesNotFaultNewerWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var held = harness.HoldNextProduction('T');
        harness.FailNextProduction('T', new IOException("the volume went away"));
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard);
        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        var newerStart = harness.Index.StartWatchingAsync('T', Token);
        Assert.IsFalse(newerStart.IsCompleted, "the start waits for the recovery's lifecycle gate");

        held.Release();
        await newerStart.WaitAsync(HangGuard);
        await harness.WaitForRecoveryAsync('T');

        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.CatchingUp, drive.Watch.CatchUpState);
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task TwoDrivesFaultTogether_RecoverConcurrently()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var heldT = harness.HoldNextProduction('T');
        var heldU = harness.HoldNextProduction('U');

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        harness.Source.WatchFor('U').FailDrive(new IOException("U's journal wrapped"));
        await Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard);
        heldT.Release();
        heldU.Release();
        await harness.WaitForRecoveryAsync('T');
        await harness.WaitForRecoveryAsync('U');

        foreach (var driveLetter in new[] { 'T', 'U' })
        {
            Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor(driveLetter).Watch.CatchUpState);
            Assert.AreEqual(2, harness.Source.TargetsFor(driveLetter).Count);
        }
    }

    [TestMethod]
    public async Task RecoveryTicketCompletion_ContinuationNotInline()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var index = harness.Index;
        index.RecoveryCompletionWrapperForTest = complete =>
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
            if (fault.Kind == WatchFaultKind.Drive && index.TryGetRecoveryCompletionForTest('T', out var completion))
            {
                _ = completion.ContinueWith(_ => observed.TrySetResult(_completingRecoveryTicket),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        };

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));

        Assert.IsFalse(await observed.Task.WaitAsync(HangGuard),
            "the ticket's continuation ran on the stack that completed it");
    }

    /// <summary>
    ///     Parks the drive's next recovery before it takes the drive's lifecycle gate, until the
    ///     returned gate is released.
    /// </summary>
    static TestGate HoldRecoveryBeforeItsGate(WatchHarness harness, char driveLetter)
    {
        var gate = harness.TrackGate();
        var held = 0;
        harness.Index.RecoveryBeforeLifecycleGateForTest = async (letter, cancellationToken) =>
        {
            if (letter == driveLetter && Interlocked.Exchange(ref held, 1) == 0)
            {
                gate.MarkEntered();
                await gate.WaitForReleaseAsync(cancellationToken);
            }
        };
        return gate;
    }
}
