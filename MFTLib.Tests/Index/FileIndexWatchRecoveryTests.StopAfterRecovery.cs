using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A stop after automatic recovery: once the restarted watch reaches <see cref="WatchState.CaughtUp" />
///     the recovery has resolved the fault, so the stop succeeds; before that the fault is still current
///     and the stop reports it once as failed.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    static async Task AssertStopSucceedsAsync(WatchHarness harness, char driveLetter)
    {
        var stop = await harness.IndexFor(driveLetter).StopWatchingAsync(CancellationToken.None).WaitAsync(HangGuard);
        Assert.AreEqual(RootOperationOutcome.Succeeded, stop.Outcome, stop.Failure?.Message);
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor(driveLetter).Watch.State);
    }

    [TestMethod]
    public async Task StopAfterRescanRecoveryCaughtUp_Succeeds()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);

        await AssertStopSucceedsAsync(harness, 'T');
    }

    [TestMethod]
    public async Task StopAfterRetriedRecoveryCaughtUp_Succeeds()
    {
        using var harness = new WatchHarness('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.SetVolumeAbsent('T', absent: false);
        harness.Clock.Advance(FileIndex.VolumeScan.RecoveryRetryDelays[0]);
        await harness.WaitForRecoveryAsync('T');
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);

        await AssertStopSucceedsAsync(harness, 'T');
    }

    [TestMethod]
    public async Task StopAfterRevivedRecoveryCaughtUp_Succeeds()
    {
        using var harness = new WatchHarness('T');
        await ExhaustRetriesWhileAbsentAsync(harness, Token);
        harness.SetVolumeAbsent('T', absent: false);
        harness.RaiseVolumeArrival();
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(2, harness.RecoveryCount('T'), "the arrival revived the recovery");
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'));

        await AssertStopSucceedsAsync(harness, 'T');
    }

    [TestMethod]
    public async Task StopAfterRecoveryRestartBeforeCaughtUp_ReportsTheFaultOnce()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var watchFailure = new IOException("T's journal wrapped");
        harness.Source.WatchFor('T').FailVolume(watchFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);

        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, Token));
        Assert.AreSame(watchFailure, failure.InnerException, "the restarted watch has not caught up, so the fault is current");
        await RootResultAssertions.NotApplicableAsync(
            () => harness.IndexFor('T').StopWatchingAsync(Token), "the fault is reported once");
    }
}
