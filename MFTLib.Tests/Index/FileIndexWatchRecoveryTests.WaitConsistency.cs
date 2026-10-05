using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A catch-up wait and <see cref="FileIndex.Drives" /> tell one story: a wait faults only once
///     the drive's status shows the fault, its recovery and any checkpoint loss, and a drive whose
///     watch is requested reads <see cref="WatchCatchUpState.CatchingUp" />, with waits following
///     the replacement, from the moment a rescan retires its watch until the replacement starts.
///     Windows are held open through the index's own test seams and <see cref="TestGate" />s.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    /// <summary>What a rescan's restart-decision seam saw of the drive in the retire-to-restart window.</summary>
    sealed class RestartWindowObservation
    {
        public DriveStatus? Status { get; set; }

        public Task? Wait { get; set; }

        public InvalidOperationException? WaitRefusal { get; set; }
    }

    /// <summary>Reads the drive and starts a catch-up wait from inside the rescan's restart window.</summary>
    static RestartWindowObservation ObserveRestartWindow(WatchHarness harness, char driveLetter,
        Action? thenInsideTheWindow = null)
    {
        var observation = new RestartWindowObservation();
        harness.Index.BeforeRestartDecisionForTest = letter =>
        {
            if (letter != driveLetter || observation.Status is not null)
            {
                return;
            }

            observation.Status = harness.DriveFor(driveLetter);
            try
            {
                observation.Wait = harness.Index.WaitForCatchUpAsync(driveLetter, CancellationToken.None);
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
        Assert.IsTrue(observation.Status.WatchRequested);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, observation.Status.WatchCatchUp,
            "a requested drive between its retired watch and the replacement is not NotStarted");
        Assert.IsNotNull(observation.Wait);
    }

    [TestMethod]
    public async Task CatchUpWait_FaultsOnlyOnceTheDriveReadsRecoveringWithItsCheckpointLoss()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var wait = index.WaitForCatchUpAsync('T', Token);
        var observedWhenSettled = wait.ContinueWith(_ => index.Drives.Single(drive => drive.DriveLetter == 'T'),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var held = harness.HoldNextProduction('T');
        bool? faultVisibleDuringCheckpointCheck = null;
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ =>
        {
            // Runs after the fault is recorded and before the recovery is queued. A wait issued
            // here must not have settled yet, or a consumer's fault path could read Faulted.
            faultVisibleDuringCheckpointCheck ??= index.WaitForCatchUpAsync('T', CancellationToken.None).IsCompleted;
            return new JournalWindow(WatchHarness.JournalId, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024);
        });

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        var observed = await observedWhenSettled.WaitAsync(HangGuard);

        Assert.IsFalse(faultVisibleDuringCheckpointCheck, "the wait faulted before the drive read Recovering");
        Assert.IsTrue(wait.IsFaulted);
        Assert.AreEqual(WatchCatchUpState.Recovering, observed.WatchCatchUp);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, observed.CheckpointLoss?.DetectedDuring);
        Assert.AreEqual("T's journal wrapped", observed.WatchFailureMessage);
        held.Release();
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task ManualRescan_BetweenRetireAndRestart_ReadsCatchingUpAndTheWaitFollowsTheReplacement()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var observation = ObserveRestartWindow(harness, 'T');

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        AssertWaitFollowsTheReplacement(observation);
        Assert.IsFalse(observation.Wait!.IsCompleted, "the replacement has not caught up yet");
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await observation.Wait.WaitAsync(HangGuard);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').WatchCatchUp);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task ManualRescan_WhoseReplacementCannotStart_FaultsTheWaitIssuedBetweenWatches()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var startFailure = new IOException("rearm failed");
        harness.Source.FailNextStartFor('T', startFailure);
        var observation = ObserveRestartWindow(harness, 'T');

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        AssertWaitFollowsTheReplacement(observation);
        var failure = await ThrowsAsync<InvalidOperationException>(() => observation.Wait!.WaitAsync(HangGuard));
        Assert.AreSame(startFailure, failure.InnerException);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUp);
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task StopBetweenRetireAndRestart_CancelsTheWaitAndStartsNothing()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Task? stop = null;
        var observation = ObserveRestartWindow(harness, 'T', () => stop = index.StopWatchingAsync('T', Token));

        await index.RescanAsync('T', Token).WaitAsync(HangGuard);

        AssertWaitFollowsTheReplacement(observation);
        await stop!.WaitAsync(HangGuard);
        await ThrowsAsync<TaskCanceledException>(() => observation.Wait!.WaitAsync(HangGuard));
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the stop withdrew the request");
        var drive = harness.DriveFor('T');
        Assert.IsFalse(drive.WatchRequested);
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.WatchCatchUp);
    }

    [TestMethod]
    public async Task DisposalBetweenRetireAndRestart_CancelsTheWaitAndStartsNothing()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Task? disposal = null;
        var observation = ObserveRestartWindow(harness, 'T', () => disposal = index.DisposeAsync().AsTask());

        await ThrowsAsync<OperationCanceledException>(() => index.RescanAsync('T', Token).WaitAsync(HangGuard));

        AssertWaitFollowsTheReplacement(observation);
        await disposal!.WaitAsync(HangGuard);
        await ThrowsAsync<TaskCanceledException>(() => observation.Wait!.WaitAsync(HangGuard));
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "disposal withdrew the request");
    }

    /// <summary>
    ///     A stop that lands after the pump marked its fault and before the pump settled the wait
    ///     does not cancel the wait: the wait reports the fault that ended the watch it followed,
    ///     and the stop rethrows that same fault once.
    /// </summary>
    [TestMethod]
    public async Task StopRacingAPumpFault_FaultsThePendingWaitWithTheWatchFault()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var producedBefore = harness.ProductionCount('T');
        var wait = index.WaitForCatchUpAsync('T', Token);
        var stop = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.PumpFaultSettlementWrapperForTest = settle =>
        {
            settle();
            stop.TrySetResult(index.StopWatchingAsync('T', Token));
        };
        var driveFailure = new IOException("T's journal wrapped");

        harness.Source.WatchFor('T').FailDrive(driveFailure);

        var waitFault = await ThrowsAsync<DriveWatchFaultException>(() => wait.WaitAsync(HangGuard));
        Assert.AreSame(driveFailure, waitFault.InnerException);
        var stopTask = await stop.Task.WaitAsync(HangGuard);
        var stopFault = await ThrowsAsync<DriveWatchFaultException>(() => stopTask.WaitAsync(HangGuard));
        Assert.AreSame(waitFault, stopFault, "the wait and the stop report the same watch fault");
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
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
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        harness.ScriptScans('T', Lost('T'), Held());
        var pumpParked = harness.TrackGate();
        var parked = 0;
        index.BeforeWatchChangedForTest = letter =>
        {
            if (letter == 'T' && Interlocked.Exchange(ref parked, 1) == 0)
            {
                pumpParked.MarkEntered();
                pumpParked.WaitForRelease();
            }
        };
        _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "parked.txt", nextUsn: 9000));
        await pumpParked.Entered.WaitAsync(HangGuard);
        harness.SetNextProducedCursor('T', WatchHarness.JournalId, nextUsn: 9000);

        var rescan = index.RescanAsync('T', Token);
        await UntilAsync(() => harness.DriveFor('T').WatchCatchUp != WatchCatchUpState.CaughtUp);
        var status = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.CatchingUp, status.WatchCatchUp);
        var wait = index.WaitForCatchUpAsync('T', Token);
        pumpParked.Release();
        await rescan.WaitAsync(HangGuard);

        var lost = await ThrowsAsync<JournalCatchUpLostException>(() => wait.WaitAsync(HangGuard));
        Assert.IsFalse(lost.RecoveryStopped);
        Assert.AreEqual(1, harness.CatchUpLossStatuses('T').Single().ConsecutiveLostCatchUps);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count, "the retry's block restarts the watch");
        await index.StopWatchingAsync('T', Token);
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
