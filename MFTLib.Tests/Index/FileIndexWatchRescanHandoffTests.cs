using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanHandoffTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task AppliedChange_DeliveredAfterCommit_CanRepeatDuringReplacementCatchUp()
    {
        using var directory = new TempDirectory("mftlib-rescan-events");
        var delivering = new TestGate();
        var source = new ScriptedWatchSource();
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheDirectory = directory.Path,
            MftSource = new MftIndexSource((request, cancellationToken) =>
            {
                SeededBlocks.Write(request.BlockPath, 1, WatchHarness.JournalIdentifier, 100, moment: SeededBlocks.SeededMoment);
                var block = BlockFile.Open(request.BlockPath, 1, out _)!;
                return Task.FromResult(new MftBlockProduceResult(block, 0));
            }, source.IndexSource)
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), directory.Path, 1)), Token);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<FileChange>();
        index.Changed += changes.Enqueue;
        await index.StartWatchingAsync(Token);
        index.Hooks.BeforeWatchChanged = _ =>
        {
            delivering.MarkEntered();
            delivering.WaitForRelease();
        };
        try
        {
            var delivered = source.WatchFor('T').Queue(WatchHarness.Batch(9, "replayed.txt", 700));
            await delivering.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            var original = index.Root(TestContext.CancellationToken).RootBlock;
            var rescan = index.RescanAsync(Token);
            Assert.AreNotSame(original, index.Root(TestContext.CancellationToken).RootBlock,
                "publication need not wait for an already applied change's delivery");
            Assert.IsEmpty(changes);
            Assert.IsEmpty(index.Search(new SearchQuery("replayed.txt", NameMatchMode.Exact), Token), "queries await the new catch-up");
            delivering.Release();
            await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
            await delivered.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.HasCount(1, changes);
            await source.WatchFor('T').Publish(WatchHarness.Batch(9, "replayed.txt", 700));
            Assert.HasCount(2, changes);
            Assert.AreEqual(WatchState.CatchingUp, index.Status.Watch.State);
            await source.WatchFor('T').Publish(new VolumeCaughtUp());
            await index.WaitForCatchUpAsync(Token);
            Assert.HasCount(1, index.Search(new SearchQuery("replayed.txt", NameMatchMode.Exact), Token));
            await index.StopWatchingAsync(Token);
        }
        finally
        {
            delivering.Release();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commit_RetiresWithPublicationThenDrainsOutsideTheWriteGate(bool cancelWhileQueued = false)
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(Token);
        var original = index.Root(TestContext.CancellationToken).RootBlock;
        var catchUp = index.WaitForCatchUpAsync(Token);
        var applying = harness.TrackGate();
        index.Hooks.ApplyJournalEntriesEntered = _ =>
        {
            applying.MarkEntered();
            applying.WaitForRelease();
        };
        var queued = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "stale.txt", 700));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var publishing = harness.TrackGate();
        index.Hooks.PublishInsideWriteGate = _ =>
        {
            Assert.AreSame(original, index.Root(TestContext.CancellationToken).RootBlock);
            Assert.IsFalse(catchUp.IsCompleted, "retirement must wait until the locked commit");
            if (cancelWhileQueued)
            {
                publishing.MarkEntered();
                publishing.WaitForRelease();
            }
        };
        harness.SetNextProducedCursor('T', 13, 9000);

        TestGate? production = null;
        if (cancelWhileQueued)
        {
            production = harness.HoldNextProduction('T');
        }

        var rescan = index.RescanAsync(Token);

        if (cancelWhileQueued)
        {
            await production!.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            production.Release();
            await publishing.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            try
            {
                using var queuedCancellation = new CancellationTokenSource();
                var cancelledWait = index.WaitForRootWriteGateForTest(new VolumeKey('T'), queuedCancellation.Token);
                await queuedCancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledWait);
            }
            finally
            {
                publishing.Release();
            }

            while (ReferenceEquals(original, index.Root(TestContext.CancellationToken).RootBlock))
            {
                await Task.Yield();
            }
        }

        Assert.AreNotSame(original, index.Root(TestContext.CancellationToken).RootBlock, "publication precedes the pump drain");
        Assert.IsFalse(rescan.IsCompleted);
        await RootResultAssertions.FailureAsync<OperationCanceledException>(() => catchUp);
        await DrainRootWriteGateForTest(index, 'T', HangGuard, TestContext.CancellationToken);
        applying.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await queued.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsEmpty(harness.Changes);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9000), harness.Source.TargetsFor('T')[1]);
        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposal_DuringProduction_EndsTheOriginalWatch(bool dispose)
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        var production = harness.HoldNextProduction('T');
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, handle.DisposeCount, "production keeps the original watch until stop or disposal");
        if (dispose)
        {
            var disposal = harness.Index.DisposeAsync().AsTask();
            await RootResultAssertions.FailureAsync<OperationCanceledException>(() => rescan);
            await disposal.WaitAsync(HangGuard, TestContext.CancellationToken);
        }
        else
        {
            await harness.IndexFor('T').StopWatchingAsync(Token);
            production.Release();
            await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
        }

        Assert.AreEqual(1, handle.DisposeCount);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedScan_HasNoRestartCheckpointAtWhichDisposalCanReplaceItsFailure(bool afterDecision)
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await harness.IndexFor('T').StartWatchingAsync(Token);
        Task? disposal = null;
        void BeginDisposal(RootKey _) => disposal = index.DisposeAsync().AsTask();
        if (afterDecision)
        {
            harness.Index.Hooks.RestartRequested = BeginDisposal;
        }
        else
        {
            harness.Index.Hooks.BeforeRestartDecision = BeginDisposal;
        }

        var producerFailure = new OperationCanceledException("production failed");
        harness.FailNextProduction('T', producerFailure);
        var failure = await RootResultAssertions.FailureAsync<OperationCanceledException>(
            () => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreSame(producerFailure, failure);
        Assert.IsNull(disposal, "a failed scan never enters a restart checkpoint");
        await harness.Index.DisposeAsync();
    }

    [TestMethod]
    public async Task FaultSettledBeforeCommit_QueuedAfterCommit_DoesNotRecoverTheReplacement()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var faultSettled = harness.TrackGate();
        harness.Index.Hooks.PumpFaultSettlementWrapper = settle =>
        {
            settle();
            faultSettled.MarkEntered();
            faultSettled.WaitForRelease();
        };
        var production = harness.HoldNextProduction('T');
        var original = harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock;
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, harness.Source.WatchFor('T').DisposeCount);
        harness.Source.WatchFor('T').FailVolume(new IOException("fault before commit"));
        await faultSettled.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var restart = harness.TrackGate();
        harness.Index.Hooks.BeforeRestartDecision = _ =>
        {
            restart.MarkEntered();
            restart.WaitForRelease();
        };
        WatchState? stateAtFault = null;
        index.WatchFaulted += _ => stateAtFault = index.Status.Watch.State;
        var publishing = harness.TrackGate();
        harness.Index.Hooks.PublishInsideWriteGate = _ => publishing.MarkEntered();
        production.Release();
        // Publication can be observed while the faulted pump is held before queuing recovery.
        await publishing.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await DrainRootWriteGateForTest(harness.Index, 'T', HangGuard, TestContext.CancellationToken);
        faultSettled.Release();
        await restart.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreNotSame(original, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(WatchState.Faulted, stateAtFault);
        restart.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(2, harness.ProductionCount('T'));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    static async Task DrainRootWriteGateForTest(FileIndex index, VolumeKey volume, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waitTask = index.WaitForRootWriteGateForTest(volume, cancellationToken);
        try
        {
            await waitTask.WaitAsync(timeout, cancellationToken);
            index.ReleaseRootWriteGateForTest(volume);
        }
        catch
        {
            _ = waitTask.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    index.ReleaseRootWriteGateForTest(volume);
                }
            }, TaskScheduler.Default);
            throw;
        }
    }
}
