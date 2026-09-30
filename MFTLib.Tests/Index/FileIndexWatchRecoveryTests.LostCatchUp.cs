using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A recovery rescan whose catch-up is lost (spec 2.6.6): the losses are scripted on the fake
///     producer's result, as in <see cref="FileIndexCatchUpLossTests" />, and no clock is involved.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    static JournalCheckpointLoss Loss(char driveLetter) => new()
    {
        DriveLetter = driveLetter,
        DetectedDuring = JournalCheckpointLossDetection.ScanCatchUp,
        Cause = JournalCheckpointLossCause.CheckpointTrimmed,
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        AllocationDelta = 4096,
        MaximumSize = 32768,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    static ScriptedScan Lost(char driveLetter, TestGate? hold = null) => new(Loss(driveLetter), Hold: hold);

    static ScriptedScan Held(TestGate? hold = null) => new(Hold: hold);

    static JournalCatchUpLostException[] CatchUpLosses(WatchHarness harness, char driveLetter) =>
        harness.Faults.Where(fault => fault.Kind == WatchFaultKind.CatchUpLost && fault.DriveLetter == driveLetter)
            .Select(fault => (JournalCatchUpLostException)fault.Exception).ToArray();

    [TestMethod]
    public async Task RecoveryRescan_LosesCatchUpTwiceThenSucceeds_ReachesCaughtUp_NoRecoveryFault()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Held());
        var producedBefore = harness.ProductionCount('T');

        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var losses = CatchUpLosses(harness, 'T');
        CollectionAssert.AreEqual(new[] { 1, 2 }, losses.Select(loss => loss.ConsecutiveLostCatchUps).ToArray());
        Assert.IsFalse(losses.Any(loss => loss.RecoveryStopped));
        CollectionAssert.AreEqual(
            new[] { WatchFaultKind.Drive, WatchFaultKind.CatchUpLost, WatchFaultKind.CatchUpLost },
            FaultKinds(harness, 'T'));
        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, drive.WatchCatchUp);
        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        await harness.Source.HandleFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task RecoveryRescan_CatchUpLostThreeTimes_StopsWithRecoveryStopped_StaysFaulted_NoFurtherRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'), Held());
        var producedBefore = harness.ProductionCount('T');

        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');

        CollectionAssert.AreEqual(
            new[]
            {
                WatchFaultKind.Drive, WatchFaultKind.CatchUpLost, WatchFaultKind.CatchUpLost,
                WatchFaultKind.CatchUpLost
            },
            FaultKinds(harness, 'T'));
        CollectionAssert.AreEqual(new[] { false, false, true },
            CatchUpLosses(harness, 'T').Select(loss => loss.RecoveryStopped).ToArray());
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.WatchCatchUp);
        Assert.AreEqual(3, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(1, harness.Source.StartsFor('T').Count);

        _ = harness.Index.Drives;
        Assert.IsFalse(harness.Index.TryGetRecoveryCompletionForTest('T', out _), "reading the drives queues no recovery");
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
    }

    [TestMethod]
    public async Task StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var held = harness.TrackGate();
        harness.ScriptScans('T', Lost('T', hold: held), Held());
        var producedBefore = harness.ProductionCount('T');
        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard);

        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        held.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the retry re-checks the watch request");
        Assert.AreEqual(1, CatchUpLosses(harness, 'T').Length);
        Assert.AreEqual(1, harness.Source.StartsFor('T').Count);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.WatchCatchUp);
        Assert.AreEqual(1, drive.ConsecutiveLostCatchUps);
        Assert.IsFalse(FaultKinds(harness, 'T').Contains(WatchFaultKind.Recovery));
    }

    [TestMethod]
    public async Task CatchUpCounts_ArePerDrive_OtherDriveRecoversNormally()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        harness.Source.HandleFor('U').FailDrive(new IOException("U's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'U');
        await harness.WaitForRecoveryAsync('T');
        await harness.WaitForRecoveryAsync('U');

        var driveT = harness.DriveFor('T');
        Assert.AreEqual(3, driveT.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchCatchUpState.Faulted, driveT.WatchCatchUp);
        Assert.IsTrue(CatchUpLosses(harness, 'T')[^1].RecoveryStopped);
        var driveU = harness.DriveFor('U');
        Assert.AreEqual(0, driveU.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, driveU.WatchCatchUp);
        Assert.AreEqual(2, harness.Source.StartsFor('U').Count);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'U'));
    }

    [TestMethod]
    public async Task ManualRescanAfterCatchUpStop_RestoresWatchAndResetsCount()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUp);

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, drive.WatchCatchUp);
        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
    }

    /// <summary>
    ///     Spec 2.6.4: a wait issued while the drive reads <see cref="WatchCatchUpState.Recovering" />
    ///     faults at once with the drive's fault, here the lost catch-up a consumer rescan is retrying.
    /// </summary>
    [TestMethod]
    public async Task WaitForCatchUp_WhileARescanRetriesALostCatchUp_FaultsAtOnceWithTheLoss()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), Held(heldRetry));
        var rescan = harness.Index.RescanAsync('T', Token);
        await heldRetry.Entered.WaitAsync(HangGuard);
        Assert.AreEqual(WatchCatchUpState.Recovering, harness.DriveFor('T').WatchCatchUp);

        var thrown = await ThrowsAsync<JournalCatchUpLostException>(
            () => harness.Index.WaitForCatchUpAsync('T', Token).WaitAsync(HangGuard));

        Assert.AreSame(CatchUpLosses(harness, 'T').Single(), thrown);
        heldRetry.Release();
        await rescan.WaitAsync(HangGuard);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
    }
}
