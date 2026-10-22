using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A recovery rescan whose catch-up is lost (spec 2.6.6): the losses are scripted on the fake
///     producer's result, as in <see cref="FileIndexCatchUpLossTests" />, and no clock is involved.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    static WatchHarnessScan Held(TestGate? hold = null) => new(Hold: hold);

    [TestMethod]
    public async Task RecoveryRescan_LosesCatchUpTwiceThenSucceeds_ReachesCaughtUp_NoRecoveryFault()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Held());
        var producedBefore = harness.ProductionCount('T');

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var losses = CatchUpLosses(harness, 'T');
        Assert.AreSequenceEqual([1, 2], [.. harness.CatchUpLossStatuses('T').Select(status => status.Watch.ConsecutiveLostCatchUps)]);
        Assert.DoesNotContain(loss => loss.IsRecoveryStopped, losses);
        Assert.AreSequenceEqual(
            [WatchFaultKind.Drive, WatchFaultKind.CatchUpLost, WatchFaultKind.CatchUpLost], FaultKinds(harness, 'T'));
        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.CatchingUp, drive.Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task RecoveryRescan_CatchUpLostThreeTimes_StopsWithRecoveryStopped_StaysFaulted_NoFurtherRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'), Held());
        var producedBefore = harness.ProductionCount('T');

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreSequenceEqual(
            [
                WatchFaultKind.Drive, WatchFaultKind.CatchUpLost, WatchFaultKind.CatchUpLost,
                WatchFaultKind.CatchUpLost
            ], FaultKinds(harness, 'T'));
        Assert.AreSequenceEqual([false, false, true], [.. CatchUpLosses(harness, 'T').Select(loss => loss.IsRecoveryStopped)]);
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchState.Faulted, drive.Watch.State);
        Assert.AreEqual(3, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(drive.Watch.IsRecoveryStopped);
        Assert.AreSequenceEqual([false, false, true], [.. harness.CatchUpLossStatuses('T').Select(status => status.Watch.IsRecoveryStopped)]);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));

        _ = harness.Index.Drives;
        Assert.IsFalse(harness.Index.TryGetRecoveryCompletionForTest('T', out _), "reading the drives queues no recovery");
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
    }

    [TestMethod]
    public async Task StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var held = harness.TrackGate();
        harness.ScriptScans('T', Lost('T', hold: held), Held());
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard);

        await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync(['T'], Token));
        held.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the retry re-checks the watch request");
        Assert.HasCount(1, CatchUpLosses(harness, 'T'));
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchState.NotStarted, drive.Watch.State);
        Assert.AreEqual(1, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(FaultKinds(harness, 'T').Contains(WatchFaultKind.Recovery));
    }

    [TestMethod]
    public async Task CatchUpCounts_ArePerDrive_OtherDriveRecoversNormally()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Index.StartWatchingAsync(['U'], Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        harness.Source.WatchFor('U').FailDrive(new IOException("U's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'U');
        await harness.WaitForRecoveryAsync('T');
        await harness.WaitForRecoveryAsync('U');

        var driveT = harness.DriveFor('T');
        Assert.AreEqual(3, driveT.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.Faulted, driveT.Watch.State);
        Assert.IsTrue(CatchUpLosses(harness, 'T')[^1].IsRecoveryStopped);
        var driveU = harness.DriveFor('U');
        Assert.AreEqual(0, driveU.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.CatchingUp, driveU.Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('U'));
        Assert.AreSequenceEqual([WatchFaultKind.Drive], FaultKinds(harness, 'U'));
    }

    [TestMethod]
    public async Task ManualRescanAfterCatchUpStop_RestoresWatchAndResetsCount()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);

        await harness.Index.RescanAsync(['T'], Token).WaitAsync(HangGuard);

        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.IsFalse(drive.Watch.IsRecoveryStopped);
        Assert.AreEqual(WatchState.CatchingUp, drive.Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
    }

    /// <summary>
    ///     Spec 2.6.4: a wait issued while the drive reads <see cref="WatchState.Recovering" />
    ///     faults at once with the drive's fault, here the lost catch-up a consumer rescan is retrying.
    /// </summary>
    [TestMethod]
    public async Task WaitForCatchUp_WhileARescanRetriesALostCatchUp_FaultsAtOnceWithTheLoss()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), Held(heldRetry));
        var rescan = harness.Index.RescanAsync(['T'], Token);
        await heldRetry.Entered.WaitAsync(HangGuard);
        Assert.AreEqual(WatchState.Recovering, harness.DriveFor('T').Watch.State);

        var failure = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.Index.WaitForCatchUpAsync(['T'], Token).WaitAsync(HangGuard));

        Assert.AreSame(CatchUpLosses(harness, 'T').Single(), failure);
        heldRetry.Release();
        await rescan.WaitAsync(HangGuard);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
    }
}
