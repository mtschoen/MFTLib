using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A stop that lands while a rescan's or a recovery's restart is about to register its
///     instance: the stop wins, with the outcome of a stop that came before the restart.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    /// <summary>
    ///     A stop that returns while the recovery's restart is about to register its instance wins,
    ///     with the outcome of a stop that came before the restart: it reports the faulted
    ///     watch's fault once in a failed result, and the restart registers nothing and starts no watch.
    /// </summary>
    [TestMethod]
    public async Task StopWhileRecoveryRegistersItsRestart_StopWins()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var beforeRegistration = HoldRestartBeforeRegistration(harness, 'T');
        var producedBefore = harness.ProductionCount('T');
        var watchFailure = new IOException("T's journal wrapped");
        harness.Source.WatchFor('T').FailDrive(watchFailure);
        await beforeRegistration.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var failure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync(['T'], Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(watchFailure, failure.InnerException, "the stop reports the fault it stopped");
        beforeRegistration.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the recovery committed its block");
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "no watch starts after the stop returned");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Drive], FaultKinds(harness, 'T'));
        await DriveResultAssertions.NotApplicableAsync(
            () => harness.Index.StopWatchingAsync(['T'], Token), "the stop's cleared request stands");
    }

    /// <summary>
    ///     The same window in a consumer rescan's restart of a faulted drive: the stop reports the
    ///     faulted watch's fault in a failed result, and the rescan starts no watch.
    /// </summary>
    [TestMethod]
    public async Task StopWhileRescanOfAFaultedDriveRegistersItsRestart_ReportsFailedResultWithTheFault()
    {
        using var harness = new WatchHarness('T');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        var watchFailure = new IOException("T's journal wrapped");
        harness.Source.WatchFor('T').FailDrive(watchFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        var beforeRegistration = HoldRestartBeforeRegistration(harness, 'T');
        var rescan = harness.Index.RescanAsync(['T'], Token);
        await beforeRegistration.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var failure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync(['T'], Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(watchFailure, failure.InnerException, "the stop reports the fault it stopped");
        beforeRegistration.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(1, harness.Source.TargetsFor('T'), "no watch starts after the stop returned");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.NotApplicableAsync(
            () => harness.Index.StopWatchingAsync(['T'], Token), "the stop's cleared request stands");
    }

    /// <summary>The same window in a consumer rescan's restart of a healthy drive.</summary>
    [TestMethod]
    public async Task StopWhileRescanRegistersItsRestart_StopWins()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var beforeRegistration = HoldRestartBeforeRegistration(harness, 'T');
        var rescan = harness.Index.RescanAsync(['T'], Token);
        await beforeRegistration.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Index.StopWatchingAsync(['T'], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        beforeRegistration.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(1, harness.Source.TargetsFor('T'), "no watch starts after the stop returned");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.NotApplicableAsync(
            () => harness.Index.StopWatchingAsync(['T'], Token), "the stop's cleared request stands");
    }

    /// <summary>
    ///     Parks the drive's next restart (a rescan's or a recovery's) just before it registers its
    ///     instance, until the returned gate is released.
    /// </summary>
    static TestGate HoldRestartBeforeRegistration(WatchHarness harness, char driveLetter)
    {
        var gate = harness.TrackGate();
        var held = 0;
        harness.Index.Hooks.RestartBeforeRegistration = async letter =>
        {
            if (letter == driveLetter && Interlocked.Exchange(ref held, 1) == 0)
            {
                gate.MarkEntered();
                await gate.WaitForReleaseAsync(CancellationToken.None).WaitAsync(HangGuard);
            }
        };
        return gate;
    }
}
