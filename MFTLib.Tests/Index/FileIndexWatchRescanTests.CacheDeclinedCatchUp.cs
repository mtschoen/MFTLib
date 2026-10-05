using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>The no-list catch-up wait over a drive a cache-only open declined and a rescan adopted.</summary>
public partial class FileIndexWatchRescanTests
{
    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_AggregateWaitForCatchUp_WaitsForAdoptedDrive()
    {
        var source = new ScriptedWatchSource();
        var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token)
            .WaitAsync(HangGuard);
        try
        {
            await index.StartWatchingAsync('U', Token).WaitAsync(HangGuard);

            // U catches up at once; T is adopted by the rescan and joins the watch by its own start.
            await source.WatchFor('U').Publish(new DriveCaughtUp());
            await index.RescanAsync('T', Token).WaitAsync(HangGuard);
            await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);

            // The no-list wait covers the adopted drive rather than completing on U alone.
            var aggregateWait = index.WaitForCatchUpAsync(Token);
            Assert.IsFalse(aggregateWait.IsCompleted);

            await source.WatchFor('T').Publish(new DriveCaughtUp());
            var results = await aggregateWait.WaitAsync(HangGuard);
            Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

            var stopped = await index.StopWatchingAsync(Token).WaitAsync(HangGuard);
            Assert.IsTrue(stopped.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
        }
        finally
        {
            await index.DisposeAsync().AsTask().WaitAsync(HangGuard);
        }
    }
}
