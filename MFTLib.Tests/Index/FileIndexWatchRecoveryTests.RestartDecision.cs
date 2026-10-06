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
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Task? stop = null;
        index.RestartRequestedForTest = letter =>
        {
            if (letter == 'T' && stop is null)
            {
                stop = index.StopWatchingAsync('T', Token);
            }
        };
        var registrationsReached = 0;
        harness.Index.RestartBeforeRegistrationForTest = _ =>
        {
            Interlocked.Increment(ref registrationsReached);
            return Task.CompletedTask;
        };

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        Assert.IsNotNull(stop, "the restart read the watch as still requested");
        await stop.WaitAsync(HangGuard);
        Assert.AreEqual(0, registrationsReached, "the start's first step already saw the stop");
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "no watch starts after the stop");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUpState);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StopWatchingAsync('T', Token), "the stop's cleared request stands").WaitAsync(HangGuard);
    }
}
