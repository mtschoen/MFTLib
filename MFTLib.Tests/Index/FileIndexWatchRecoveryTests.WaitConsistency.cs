using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A catch-up wait and <see cref="FileIndex.Status" /> tell one story: a wait faults only once
///     the drive's status shows the fault, its recovery and any checkpoint loss, and a drive whose
///     watch is requested reads <see cref="WatchState.CatchingUp" />, with waits following
///     the replacement, from the moment a rescan retires its watch until the replacement starts.
///     Windows are held open through the index's own test seams and <see cref="TestGate" />s.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    /// <summary>What a rescan's restart-decision seam saw of the drive in the retire-to-restart window.</summary>
    sealed class RestartWindowObservation
    {
        public RootStatus? Status { get; set; }

        public Task<DriveOperationResult>? Wait { get; set; }

        public InvalidOperationException? WaitRefusal { get; set; }
    }

    /// <summary>Reads the drive and starts a catch-up wait from inside the rescan's restart window.</summary>
    static RestartWindowObservation ObserveRestartWindow(WatchHarness harness, char driveLetter,
        Action? thenInsideTheWindow = null)
    {
        var observation = new RestartWindowObservation();
        var index = harness.IndexFor(driveLetter);
        index.Hooks.BeforeRestartDecision = letter =>
        {
            if (letter != new VolumeKey(driveLetter) || observation.Status is not null)
            {
                return;
            }

            observation.Status = harness.DriveFor(driveLetter);
            try
            {
                observation.Wait = index.WaitForCatchUpAsync(CancellationToken.None);
            }
            catch (InvalidOperationException refusal)
            {
                observation.WaitRefusal = refusal;
            }

            thenInsideTheWindow?.Invoke();
        };
        return observation;
    }

    static void AssertWaitFollowsTheReplacement(RestartWindowObservation observation)
    {
        Assert.IsNotNull(observation.Status, "the rescan reached its restart decision");
        Assert.IsNull(observation.WaitRefusal, "a requested drive between watches still has a catch-up to wait for");
        Assert.IsTrue(observation.Status.Watch.IsRequested);
        Assert.AreEqual(WatchState.CatchingUp, observation.Status.Watch.State,
            "a requested drive between its retired watch and the replacement is not NotStarted");
        Assert.IsNotNull(observation.Wait);
    }

    [TestMethod]
    public async Task CatchUpWait_FaultsOnlyOnceTheDriveReadsRecoveringWithItsCheckpointLoss()
    {
        var indexBox = new StrongBox<FileIndex?>();
        bool? faultVisibleDuringCheckpointCheck = null;
        using var journal = JournalIsolation.OverrideJournalWindow(_ =>
        {
            // Runs after the fault is recorded and before the recovery is queued. A wait issued
            // here must not have settled yet, or a consumer's fault path could read Faulted.
            faultVisibleDuringCheckpointCheck ??= indexBox.Value!.WaitForCatchUpAsync(CancellationToken.None).IsCompleted;
            return new SyntheticJournalWindow(WatchHarness.JournalIdentifier, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024);
        });
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        indexBox.Value = index;
        await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var wait = index.WaitForCatchUpAsync(Token);
        var observedWhenSettled = wait.ContinueWith(_ => index.Status,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var held = harness.HoldNextProduction('T');

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        var observed = await observedWhenSettled.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsFalse(faultVisibleDuringCheckpointCheck, "the wait faulted before the drive read Recovering");
        Assert.AreEqual(DriveOperationOutcome.Failed, (await wait).Outcome);
        Assert.AreEqual(WatchState.Recovering, observed.Watch.State);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, observed.Watch.CheckpointLoss?.DetectedDuring);
        Assert.AreEqual("T's journal wrapped", observed.Watch.FailureMessage);
        held.Release();
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ManualRescan_BetweenRetireAndRestart_ReadsCatchingUpAndTheWaitFollowsTheReplacement()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var observation = ObserveRestartWindow(harness, 'T');

        await harness.Index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertWaitFollowsTheReplacement(observation);
        Assert.IsFalse(observation.Wait!.IsCompleted, "the replacement has not caught up yet");
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await observation.Wait.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ManualRescan_WhoseReplacementCannotStart_FaultsTheWaitIssuedBetweenWatches()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var startFailure = new IOException("rearm failed");
        harness.Source.FailNextStartFor('T', startFailure);
        var observation = ObserveRestartWindow(harness, 'T');

        await harness.Index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertWaitFollowsTheReplacement(observation);
        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => observation.Wait!.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(startFailure, failure.InnerException);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task StopBetweenRetireAndRestart_CancelsTheWaitAndStartsNothing()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Task? stop = null;
        var observation = ObserveRestartWindow(harness, 'T', () => stop = index.StopWatchingAsync(Token));

        await index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertWaitFollowsTheReplacement(observation);
        await stop!.WaitAsync(HangGuard, TestContext.CancellationToken);
        await DriveResultAssertions.FailureAsync<TaskCanceledException>(() => observation.Wait!.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the stop withdrew the request");
        var drive = harness.DriveFor('T');
        Assert.IsFalse(drive.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, drive.Watch.State);
    }

    [TestMethod]
    public async Task DisposalBetweenRetireAndRestart_CancelsTheWaitAndStartsNothing()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Task? disposal = null;
        var observation = ObserveRestartWindow(harness, 'T', () => disposal = index.DisposeAsync().AsTask());

        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        AssertWaitFollowsTheReplacement(observation);
        await disposal!.WaitAsync(HangGuard, TestContext.CancellationToken);
        await Assert.ThrowsAsync<TaskCanceledException>(() => observation.Wait!.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "disposal withdrew the request");
    }

    /// <summary>
    ///     A stop that lands after the pump marked its fault and before the pump settled the wait
    ///     does not cancel the wait: the wait reports the fault that ended the watch it followed,
    ///     and the stop reports that same fault once in a failed result.
    /// </summary>
    [TestMethod]
    public async Task StopRacingAPumpFault_SettlesThePendingWaitWithTheWatchFailure()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var producedBefore = harness.ProductionCount('T');
        var wait = index.WaitForCatchUpAsync(Token);
        var stop = new TaskCompletionSource<Task<DriveOperationResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Hooks.PumpFaultSettlementWrapper = settle =>
        {
            settle();
            stop.TrySetResult(index.StopWatchingAsync(Token));
        };
        var driveFailure = new IOException("T's journal wrapped");

        harness.Source.WatchFor('T').FailDrive(driveFailure);

        var waitFault = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => wait.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(driveFailure, waitFault.InnerException);
        var stopTask = await stop.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        var stopFault = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => stopTask.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(waitFault, stopFault, "the wait and the stop report the same watch fault");
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    /// <summary>
    ///     A lost catch-up found after the first publication retired the watch faults a wait issued
    ///     between that retirement and the loss being recorded, as a wait issued during the retry
    ///     does. The old pump is parked after applying a batch, which holds the rescan in that
    ///     window while it drains the pump.
    /// </summary>
    [TestMethod]
    public async Task LostCatchUpAfterRetire_FaultsTheWaitIssuedBetweenWatches()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        harness.ScriptScans('T', Lost('T'), Held());
        var pumpParked = harness.TrackGate();
        var parked = 0;
        index.Hooks.BeforeWatchChanged = letter =>
        {
            if (letter == new VolumeKey('T') && Interlocked.Exchange(ref parked, 1) == 0)
            {
                pumpParked.MarkEntered();
                pumpParked.WaitForRelease();
            }
        };
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "parked.txt", nextUsn: 9000));
        await pumpParked.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        harness.SetNextProducedCursor('T', WatchHarness.JournalIdentifier, nextUsn: 9000);

        var rescan = index.RescanAsync(Token);
        await UntilAsync(() => harness.DriveFor('T').Watch.State != WatchState.CaughtUp);
        var status = harness.DriveFor('T');
        Assert.AreEqual(WatchState.CatchingUp, status.Watch.State);
        var wait = index.WaitForCatchUpAsync(Token);
        pumpParked.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);

        var lost = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(() => wait.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.IsFalse(lost.IsRecoveryStopped);
        Assert.AreEqual(1, harness.CatchUpLossStatuses('T').Single().Watch.ConsecutiveLostCatchUps);
        Assert.HasCount(2, harness.Source.TargetsFor('T'), "the retry's block restarts the watch");
        await index.StopWatchingAsync(Token);
    }

    /// <summary>Polls a condition another thread makes true, bounded by the hang guard.</summary>
    static async Task UntilAsync(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        while (!condition())
        {
            await Task.Delay(1, guard.Token);
        }
    }
}
