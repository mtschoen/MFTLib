using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     How a lost catch-up sits inside its scan operation: the operation raises the fault while it
///     still holds the root's lifecycle gate, a superseded lost block leaves no file behind, and a
///     start queued behind the operation judges the block the operation left, not the one it
///     replaced.
/// </summary>
public partial class FileIndexCatchUpLossTests
{
    [TestMethod]
    public async Task CatchUpLost_HandlerRunsWithLifecycleGateHeld()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), new WatchHarnessScan(Hold: heldRetry));
        var index = harness.Index;
        var token = Token;
        var queuedRescan = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind != WatchFaultKind.CatchUpLost)
            {
                return;
            }

            // The continuation inherits the delivery marker, which stays active until every
            // subscriber has returned. The held retry scan enters Produce only after RaiseWatchFaulted
            // has returned, while the scan operation still holds the lifecycle gate, so waiting for it
            // releases the queued rescan outside the handler and before the gate frees.
            _ = Task.Run(async () =>
            {
                await heldRetry.Entered;
                queuedRescan.TrySetResult(index.RescanAsync(token));
            }, TestContext.CancellationToken);
        };
        var producedBefore = harness.ProductionCount('T');

        var operation = index.RescanAsync(Token);
        await heldRetry.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var queued = await queuedRescan.Task.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsFalse(queued.IsCompleted, "the queued rescan waits for the operation's lifecycle gate");
        Assert.IsFalse(harness.Index.AreRootGatesFreeForTest(new VolumeKey('T')));
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "the queued rescan has not scanned");

        heldRetry.Release();
        await operation.WaitAsync(HangGuard, TestContext.CancellationToken);
        await queued.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
    }

    [TestMethod]
    public async Task CatchUpLost_LeavesNoBlockFileBehind()
    {
        using var cache = new LossScriptedCache();
        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));

        await index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(4, cache.Productions, "the open's scan, then two lost catch-ups and the scan that held");
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        Assert.AreSequenceEqual([LossScriptedCache.CanonicalBlockName], cache.BlockFileNames(), "each superseded block's file is gone once its snapshot is released");
    }

    [TestMethod]
    public async Task StartDuringRescanOfUnresumableRoot_JudgesTheFreshBlock()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(() => harness.IndexFor('T').RescanAsync(Token));
        var heldScan = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', journalIdentifier: 21, nextUsn: 7700);

        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await heldScan.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var start = harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.IsFalse(start.IsCompleted, "the start waits for the rescan's lifecycle gate");
        heldScan.Release();

        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await start.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(new IndexWatchTarget('T', 21, 7700), harness.Source.TargetsFor('T').Single());
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task Rescan_ProducerFailureWithNoMessage_ReportsFailureNamingTheRootAndKeepsThePreviousFailure()
    {
        using var harness = new WatchHarness('T');
        harness.FailNextProduction('T', new MessagelessException("the first scan failed", new IOException()));
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual("the first scan failed", harness.RootFor('T').Failure?.Message);

        harness.FailNextProduction('T', new MessagelessException());
        var messageless = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.Contains("Root T failed without a failure message", messageless.Message);
        Assert.AreEqual("the first scan failed", harness.RootFor('T').Failure?.Message);
        Assert.AreEqual(RootState.Ready, harness.RootFor('T').State);

        harness.FailNextProduction('T', new MessagelessException("the third scan failed"));
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual("the third scan failed", harness.RootFor('T').Failure?.Message);
    }

    /// <summary>A producer failure whose <see cref="Message" /> can be null, which no framework exception produces.</summary>
    sealed class MessagelessException : Exception
    {
        public MessagelessException()
        {
        }

        public MessagelessException(string message)
            : base(message)
        {
            Message = message;
        }

        public MessagelessException(string message, Exception innerException)
            : base(message, innerException)
        {
            Message = message;
        }

        public override string Message { get; } = null!;
    }
}
