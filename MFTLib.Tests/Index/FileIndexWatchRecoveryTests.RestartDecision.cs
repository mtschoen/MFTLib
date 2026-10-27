using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A stop that lands after a rescan's restart has read that the watch is still requested,
///     but before the start re-reads it: the stop wins, as it does when it lands just before the
///     restart registers its instance.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    [TestMethod]
    public async Task StopAfterTheRescanDecidedToRestart_StopWins()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync([new VolumeKey('T')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Task? stop = null;
        index.Hooks.RestartRequested = letter =>
        {
            if (letter == new VolumeKey('T') && stop is null)
            {
                stop = index.StopWatchingAsync([new VolumeKey('T')], Token);
            }
        };
        var registrationsReached = 0;
        harness.Index.Hooks.RestartBeforeRegistration = _ =>
        {
            Interlocked.Increment(ref registrationsReached);
            return Task.CompletedTask;
        };

        await harness.Index.RescanAsync([new VolumeKey('T')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsNotNull(stop, "the restart read the watch as still requested");
        await stop.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, registrationsReached, "the start's first step already saw the stop");
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "no watch starts after the stop");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StopWatchingAsync([new VolumeKey('T')], Token), "the stop's cleared request stands").WaitAsync(HangGuard, TestContext.CancellationToken);
    }
}
