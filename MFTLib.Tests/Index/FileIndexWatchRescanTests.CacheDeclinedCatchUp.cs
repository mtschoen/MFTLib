using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>The no-list catch-up wait over a root a cache-only open declined and a rescan adopted.</summary>
public sealed partial class FileIndexWatchRescanTests
{
    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhileWatching_AggregateWaitForCatchUp_WaitsForAdoptedRoot()
    {
        var source = new ScriptedWatchSource();
        var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: false), Token)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        try
        {
            await index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
            await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

            // The catch-up wait covers the adopted root.
            var aggregateWait = index.WaitForCatchUpAsync(Token);
            Assert.IsFalse(aggregateWait.IsCompleted);

            await source.WatchFor('T').Publish(new VolumeCaughtUp());
            var result = await aggregateWait.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);

            var stopped = await index.StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(RootOperationOutcome.Succeeded, stopped.Outcome);
        }
        finally
        {
            await index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        }
    }
}
