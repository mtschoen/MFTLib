using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Automatic recovery hears volume arrivals: an arrival ends a retry pause once the volume reads
///     ready and revives a recovery that gave up on an absent volume. Arrivals are raised through the
///     harness's own signal and every pause runs on its fake clock, so no test depends on a device
///     event or on real time.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    /// <summary>Faults T while its volume is absent and lets every retry fail, leaving T Faulted(Recovery).</summary>
    static async Task ExhaustRetriesWhileAbsentAsync(WatchHarness harness, CancellationToken token)
    {
        await FaultVolumeWhileAbsentAsync(harness, token);
        foreach (var delay in FileIndex.VolumeScan.RecoveryRetryDelays)
        {
            await harness.Clock.WaitForPauseAsync(token).WaitAsync(HangGuard, token);
            harness.Clock.Advance(delay);
        }

        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');
    }

    [TestMethod]
    public async Task ArrivalDuringPause_VolumeReady_RescansWithoutTheClockAndReachesCaughtUp()
    {
        using var harness = new WatchHarness('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);

        harness.SetVolumeAbsent('T', absent: false);
        harness.RaiseVolumeArrival();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.Clock.PausesBegun, "the clock never moved, so only the first pause began");
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ArrivalDuringPause_VolumeStillAbsent_KeepsThePauseAndTheSchedule()
    {
        using var harness = new WatchHarness('T');
        var producedBefore = harness.ProductionCount('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.ForgetVolumeFactsReads();

        harness.RaiseVolumeArrival();
        await harness.WaitForVolumeFactsReadAsync(Token);

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "an arrival over an absent volume probes it and does not rescan");
        for (var arrival = 0; arrival < 10; arrival++)
        {
            harness.RaiseVolumeArrival();
        }

        Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'), "no premature Recovery fault");

        harness.SetVolumeAbsent('T', absent: false);
        harness.Clock.Advance(FileIndex.VolumeScan.RecoveryRetryDelays[0]);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "the pause still ends on its own deadline");
        Assert.AreEqual(1, harness.Clock.PausesBegun);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ArrivalAfterRetriesExhausted_VolumeBack_RevivesRecoveryToCaughtUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.IndexFor('T'));
        await ExhaustRetriesWhileAbsentAsync(harness, Token);
        Assert.IsTrue(harness.ArrivalRegistration.IsHeld, "a root that gave up on an absent volume keeps listening");

        harness.SetVolumeAbsent('T', absent: false);
        harness.RaiseVolumeArrival();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(2, harness.RecoveryCount('T'), "the arrival queued a fresh recovery");
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'));
        Assert.AreSequenceEqual(
            [WatchState.Faulted, WatchState.Recovering, WatchState.Faulted, WatchState.Recovering, WatchState.CatchingUp, WatchState.CaughtUp],
            [.. recorder.StatesFor('T').Select(change => change.Watch.State).SkipWhile(state => state != WatchState.Faulted)],
            "the revival publishes Recovering before it rescans");
        Assert.IsFalse(harness.ArrivalRegistration.IsHeld, "nothing listens once the root recovered");
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ArrivalDuringFinalAbsentProbe_IsNotLost_RootRecoversWithoutAnotherArrival()
    {
        using var harness = new WatchHarness('T');
        var producedBefore = harness.ProductionCount('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);
        var delays = FileIndex.VolumeScan.RecoveryRetryDelays;
        for (var retry = 0; retry < delays.Length; retry++)
        {
            await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
            if (retry == delays.Length - 1)
            {
                // The last retry's scan fails, and its probe of the volume captures it absent; the
                // volume returns and announces itself before the probe hands that snapshot back.
                harness.ReturnVolumeDuringNextProbe('T');
            }

            harness.Clock.Advance(delays[retry]);
        }

        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(2, harness.RecoveryCount('T'), "the arrival the exhausted recovery held revives the root");
        Assert.AreEqual(delays.Length + 2, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(delays.Length, harness.Clock.PausesBegun, "no pause, and no clock advance, was needed");
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ArrivalDuringProbeAfterReadyReattemptFailed_IsNotLost_RootRecovers()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        var secondScan = harness.TrackGate();
        harness.ScriptScans('T', new WatchHarnessScan(Failure: new IOException("T vanished mid-scan")),
            new WatchHarnessScan(Failure: new IOException("T vanished again"), Hold: secondScan));

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await secondScan.Entered.WaitAsync(HangGuard, Token);
        harness.ReturnVolumeDuringNextProbe('T');
        secondScan.Release();
        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(2, harness.RecoveryCount('T'), "the arrival held at the hand-off revives the root");
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(0, harness.Clock.PausesBegun);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task NoArrivalAfterRetriesExhausted_RootStaysFaulted()
    {
        using var harness = new WatchHarness('T');
        var producedBefore = harness.ProductionCount('T');
        await ExhaustRetriesWhileAbsentAsync(harness, Token);

        harness.SetVolumeAbsent('T', absent: false);

        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
        Assert.AreEqual(FileIndex.VolumeScan.RecoveryRetryDelays.Length + 1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(1, harness.RecoveryCount('T'));
    }

    [TestMethod]
    public async Task ArrivalAfterRetriesExhausted_VolumeStillAbsent_RetriesOnTheScheduleAgain()
    {
        using var harness = new WatchHarness('T');
        await ExhaustRetriesWhileAbsentAsync(harness, Token);
        var producedBefore = harness.ProductionCount('T');

        harness.RaiseVolumeArrival();
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.RaiseVolumeArrival();

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "one scan, then the first pause of the fresh schedule");
        Assert.AreEqual(2, harness.RecoveryCount('T'), "an arrival during the revived recovery does not queue another");
        Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);

        var stopped = await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, Token);
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(RootOperationOutcome.Failed, stopped.Outcome, "stop returns the outstanding fault once");
    }

    [TestMethod]
    public async Task ArrivalForRootFaultedForAnotherReason_DoesNothing()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        harness.ScriptScans('T', new WatchHarnessScan(Failure: new IOException("the block would not parse")),
            new WatchHarnessScan(Failure: new IOException("the block still would not parse")));
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.IsFalse(harness.ArrivalRegistration.IsHeld, "a recovery that failed with its volume present stops listening");

        harness.RaiseVolumeArrival();

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task ArrivalForChannelFault_DoesNothing()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.SetVolumeAbsent('T', absent: true);
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').LoseChannel(new IOException("the broker pipe closed"));
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');

        harness.SetVolumeAbsent('T', absent: false);
        harness.RaiseVolumeArrival();

        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(0, harness.ArrivalRegistration.Registrations, "a channel fault never listens for arrivals");
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task ArrivalAfterConsumerStop_DoesNothing()
    {
        using var harness = new WatchHarness('T');
        await ExhaustRetriesWhileAbsentAsync(harness, Token);
        var producedBefore = harness.ProductionCount('T');
        _ = await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, Token);
        Assert.IsFalse(harness.ArrivalRegistration.IsHeld, "a stop releases the arrival subscription");

        harness.SetVolumeAbsent('T', absent: false);
        harness.RaiseVolumeArrival();

        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task ArrivalAfterConsumerRescan_DoesNotReviveTheOldRecovery()
    {
        using var harness = new WatchHarness('T');
        await ExhaustRetriesWhileAbsentAsync(harness, Token);
        harness.SetVolumeAbsent('T', absent: false);
        var rescanned = await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, Token);
        var producedBefore = harness.ProductionCount('T');

        harness.RaiseVolumeArrival();

        Assert.AreEqual(RootOperationOutcome.Succeeded, rescanned.Outcome);
        Assert.AreEqual(0, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.IsFalse(harness.ArrivalRegistration.IsHeld);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopDuringArrivalTriggeredRescan_Wins()
    {
        using var harness = new WatchHarness('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.SetVolumeAbsent('T', absent: false);
        var production = harness.HoldNextProduction('T');

        harness.RaiseVolumeArrival();
        await production.Entered.WaitAsync(HangGuard, Token);
        var stopping = harness.IndexFor('T').StopWatchingAsync(Token);
        production.Release();
        var stopped = await stopping.WaitAsync(HangGuard, Token);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(RootOperationOutcome.Failed, stopped.Outcome, "stop returns the watch's fault once");
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State, "stop prevents the restart");
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the watch is never restarted");
        Assert.IsFalse(harness.ArrivalRegistration.IsHeld);
    }

    [TestMethod]
    public async Task ConsumerRescanDuringArrivalRevivedPause_Wins()
    {
        using var harness = new WatchHarness('T');
        await ExhaustRetriesWhileAbsentAsync(harness, Token);
        harness.RaiseVolumeArrival();
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.SetVolumeAbsent('T', absent: false);

        var rescanned = await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, Token);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(RootOperationOutcome.Succeeded, rescanned.Outcome);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'),
            "the superseded revival raises nothing");
        Assert.IsFalse(harness.ArrivalRegistration.IsHeld);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task ArrivalRegistrationRefused_ReportsDiagnostic_AndRetriesOnTheSchedule()
    {
        using var harness = new WatchHarness('T');
        harness.ArrivalRegistration.Failure = new System.ComponentModel.Win32Exception(31, "notifications refused");
        await FaultVolumeWhileAbsentAsync(harness, Token);
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);

        harness.SetVolumeAbsent('T', absent: false);
        harness.RaiseVolumeArrival();
        harness.Clock.Advance(FileIndex.VolumeScan.RecoveryRetryDelays[0]);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.AreEqual(0, harness.ArrivalRegistration.Registrations);
        Assert.IsTrue(harness.Diagnostics.Any(line => line.Contains("volume arrival notifications are unavailable") &&
                                                      line.Contains("notifications refused")),
            string.Join(Environment.NewLine, harness.Diagnostics));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task DisposalDuringArrivalRevivedPause_EndsTheRecovery()
    {
        var harness = new WatchHarness('T');
        try
        {
            await ExhaustRetriesWhileAbsentAsync(harness, Token);
            harness.RaiseVolumeArrival();
            await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
            var recovery = harness.RecoveryCompletion('T');

            await harness.IndexFor('T').DisposeAsync().AsTask().WaitAsync(HangGuard, Token);

            Assert.IsTrue(recovery.IsCompletedSuccessfully, "disposal waits for the revived recovery");
            Assert.IsFalse(harness.ArrivalRegistration.IsHeld);
            Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'));
        }
        finally
        {
            harness.Dispose();
        }
    }
}
