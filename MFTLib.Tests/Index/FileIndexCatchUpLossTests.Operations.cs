using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     How a lost catch-up sits inside its scan operation: the operation raises the fault while it
///     still holds the drive's lifecycle gate, a superseded lost block leaves no file behind, and a
///     start queued behind the operation judges the block the operation left, not the one it
///     replaced.
/// </summary>
public partial class FileIndexCatchUpLossTests
{
    [TestMethod]
    public async Task CatchUpLost_HandlerRunsWithLifecycleGateHeld()
    {
        using var harness = new WatchHarness('T');
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), new ScriptedScan(Hold: heldRetry));
        var index = harness.Index;
        var token = Token;
        var queuedRescan = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerReturning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind != WatchFaultKind.CatchUpLost)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await handlerReturning.Task;
                queuedRescan.TrySetResult(index.RescanAsync('T', token));
            });
            handlerReturning.TrySetResult();
        };
        var producedBefore = harness.ProductionCount('T');

        var operation = index.RescanAsync('T', Token);
        await heldRetry.Entered.WaitAsync(HangGuard);
        var queued = await queuedRescan.Task.WaitAsync(HangGuard);

        Assert.IsFalse(queued.IsCompleted, "the queued rescan waits for the operation's lifecycle gate");
        Assert.IsFalse(harness.Index.AreDriveGatesFreeForTest('T'));
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "the queued rescan has not scanned");

        heldRetry.Release();
        await operation.WaitAsync(HangGuard);
        await queued.WaitAsync(HangGuard);
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
    }

    [TestMethod]
    public async Task CatchUpLost_LeavesNoBlockFileBehind()
    {
        using var cache = new LossScriptedCache();
        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));

        await index.RescanAsync('T', Token).WaitAsync(HangGuard);
        Assert.AreEqual(4, cache.Productions, "the open's scan, then two lost catch-ups and the scan that held");
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        CollectionAssert.AreEqual(new[] { LossScriptedCache.CanonicalBlockName }, cache.BlockFileNames(),
            "each superseded block's file is gone once its snapshot is released");
    }

    [TestMethod]
    public async Task StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await ThrowsAsync<JournalCatchUpLostException>(() => harness.Index.RescanAsync('T', Token));
        var heldScan = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', journalId: 21, nextUsn: 7700);

        var rescan = harness.Index.RescanAsync('T', Token);
        await heldScan.Entered.WaitAsync(HangGuard);
        var start = harness.Index.StartWatchingAsync('T', Token);
        Assert.IsFalse(start.IsCompleted, "the start waits for the rescan's lifecycle gate");
        heldScan.Release();

        await rescan.WaitAsync(HangGuard);
        await start.WaitAsync(HangGuard);
        Assert.AreEqual(new IndexWatchTarget('T', 21, 7700), harness.Source.StartsFor('T').Single());
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task Rescan_ProducerFailureWithNoMessage_ClearsTheDrivesReportedFailureMessage()
    {
        using var harness = new WatchHarness('T');
        harness.FailNextProduction('T', new MessagelessException("the first scan failed", new IOException()));
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));
        Assert.AreEqual("the first scan failed", harness.DriveFor('T').MftProducerFailureMessage);

        harness.FailNextProduction('T', new MessagelessException());
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));
        Assert.IsNull(harness.DriveFor('T').MftProducerFailureMessage);
        Assert.AreEqual(DriveState.Ready, harness.DriveFor('T').State);

        harness.FailNextProduction('T', new MessagelessException("the third scan failed"));
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));
        Assert.AreEqual("the third scan failed", harness.DriveFor('T').MftProducerFailureMessage);
    }

    /// <summary>A producer failure whose <see cref="Message" /> can be null, which no framework exception produces.</summary>
    sealed class MessagelessException : Exception
    {
        readonly string? _message;

        public MessagelessException()
        {
        }

        public MessagelessException(string message)
            : base(message)
        {
            _message = message;
        }

        public MessagelessException(string message, Exception innerException)
            : base(message, innerException)
        {
            _message = message;
        }

        public override string Message => _message!;
    }
}
