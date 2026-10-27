using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Index;

namespace MFTLib.Tests.Index;

public partial class FileIndexPerDriveWatchTests
{
    /// <summary>
    ///     A rescan that begins while a stopped watch is still tearing down publishes its block
    ///     before awaiting that teardown, so the retiring pump's last batch can never land on the block the rescan
    ///     replaces it with.
    /// </summary>
    [TestMethod]
    public async Task Rescan_WhileAStoppedWatchIsStillRetiring_PublishesBeforeItDrains()
    {
        using var harness = new WatchHarness();
        var index = harness.Index;
        await index.StartWatchingAsync([new VolumeKey('T')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var oldHandle = harness.Source.WatchFor('T');
        var applying = harness.HoldFirstApply('T');
        _ = oldHandle.Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);
        var stopToken = alreadyCancelled.Token;
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => index.StopWatchingAsync([new VolumeKey('T')], stopToken)).WaitAsync(HangGuard, TestContext.CancellationToken);
        var producedBefore = harness.ProductionCount('T');

        var rescan = index.RescanAsync([new VolumeKey('T')], Token);

        Assert.IsFalse(rescan.IsCompleted, "the rescan waits for the retiring instance");
        Assert.AreEqual(producedBefore + 1, harness.ProductionCount('T'), "production precedes the drain");
        applying.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(producedBefore + 1, harness.ProductionCount('T'));
        Assert.AreEqual(1, oldHandle.DisposeCount, "the retiring pump disposed its handle");
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the stopped watch is not restarted");
    }
}
