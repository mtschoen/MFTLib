using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A stop that lands while a rescan's or a recovery's restart is about to register its
///     instance: the stop wins, with the outcome of a stop that came before the restart.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    /// <summary>
    ///     A stop that returns while the recovery's restart is about to register its instance wins,
    ///     with the outcome of a stop that came before the restart: it rethrows the faulted
    ///     watch's fault once, and the restart registers nothing and starts no watch.
    /// </summary>
    [TestMethod]
    public async Task StopWhileRecoveryRegistersItsRestart_StopWins()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var beforeRegistration = HoldRestartBeforeRegistration(harness, 'T');
        var producedBefore = harness.ProductionCount('T');
        var failure = new IOException("T's journal wrapped");
        harness.Source.WatchFor('T').FailDrive(failure);
        await beforeRegistration.Entered.WaitAsync(HangGuard);

        var thrown = await ThrowsAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard));
        Assert.AreSame(failure, thrown.InnerException, "the stop rethrows the fault it stopped");
        beforeRegistration.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the recovery committed its block");
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "no watch starts after the stop returned");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive }, FaultKinds(harness, 'T'));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token), "the stop's cleared request stands");
    }

    /// <summary>
    ///     The same window in a consumer rescan's restart of a faulted drive: the stop rethrows the
    ///     faulted watch's fault, and the rescan starts no watch.
    /// </summary>
    [TestMethod]
    public async Task StopWhileRescanOfAFaultedDriveRegistersItsRestart_RethrowsTheFault()
    {
        using var harness = new WatchHarness('T');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        var failure = new IOException("T's journal wrapped");
        harness.Source.WatchFor('T').FailDrive(failure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        var beforeRegistration = HoldRestartBeforeRegistration(harness, 'T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await beforeRegistration.Entered.WaitAsync(HangGuard);

        var thrown = await ThrowsAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard));
        Assert.AreSame(failure, thrown.InnerException, "the stop rethrows the fault it stopped");
        beforeRegistration.Release();
        await rescan.WaitAsync(HangGuard);

        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "no watch starts after the stop returned");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token), "the stop's cleared request stands");
    }

    /// <summary>The same window in a consumer rescan's restart of a healthy drive.</summary>
    [TestMethod]
    public async Task StopWhileRescanRegistersItsRestart_StopWins()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var beforeRegistration = HoldRestartBeforeRegistration(harness, 'T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await beforeRegistration.Entered.WaitAsync(HangGuard);

        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
        beforeRegistration.Release();
        await rescan.WaitAsync(HangGuard);

        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "no watch starts after the stop returned");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token), "the stop's cleared request stands");
    }

    /// <summary>
    ///     Parks the drive's next restart (a rescan's or a recovery's) just before it registers its
    ///     instance, until the returned gate is released.
    /// </summary>
    static TestGate HoldRestartBeforeRegistration(WatchHarness harness, char driveLetter)
    {
        var gate = harness.TrackGate();
        var held = 0;
        harness.Index.RestartBeforeRegistrationForTest = async letter =>
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
