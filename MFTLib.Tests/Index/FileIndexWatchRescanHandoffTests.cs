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
        var directory = Directory.CreateTempSubdirectory("mftlib-rescan-events-");
        var delivering = new TestGate();
        try
        {
            var source = new ScriptedWatchSource();
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', directory.FullName, 1)],
                CacheDirectory = directory.FullName,
                MftSource = new MftIndexSource((request, cancellationToken) =>
                {
                    SeededBlocks.Write(request.BlockPath, 1, WatchHarness.JournalIdentifier, 100, moment: SeededBlocks.SeededMoment);
                    var block = BlockFile.Open(request.BlockPath, 1, out _)!;
                    return Task.FromResult(new MftBlockProduceResult(block, 0));
                }, source.IndexSource)
            }, Token);
            var changes = new System.Collections.Concurrent.ConcurrentQueue<FileChange>();
            index.Changed += changes.Enqueue;
            await index.StartWatchingAsync(['T'], Token);
            index.Hooks.BeforeWatchChanged = _ =>
            {
                delivering.MarkEntered();
                delivering.WaitForRelease();
            };
            try
            {
                var delivered = source.WatchFor('T').Queue(WatchHarness.Batch(9, "replayed.txt", 700));
                await delivering.Entered.WaitAsync(HangGuard);
                var original = index.Root('T').DriveBlock;
                var rescan = index.RescanAsync(['T'], Token);
                Assert.AreNotSame(original, index.Root('T').DriveBlock,
                    "publication need not wait for an already applied change's delivery");
                Assert.IsEmpty(changes);
                Assert.IsEmpty(index.Search(new SearchQuery("replayed.txt", NameMatchMode.Exact), Token), "queries await the new catch-up");
                delivering.Release();
                await rescan.WaitAsync(HangGuard);
                await delivered.WaitAsync(HangGuard);
                Assert.HasCount(1, changes);
                await source.WatchFor('T').Publish(WatchHarness.Batch(9, "replayed.txt", 700));
                Assert.HasCount(2, changes);
                Assert.AreEqual(WatchState.CatchingUp, index.Drives.Single().Watch.State);
                await source.WatchFor('T').Publish(new DriveCaughtUp());
                await index.WaitForCatchUpAsync(['T'], Token);
                Assert.HasCount(1, index.Search(new SearchQuery("replayed.txt", NameMatchMode.Exact), Token));
                await index.StopWatchingAsync(['T'], Token);
            }
            finally
            {
                delivering.Release();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Commit_RetiresWithPublicationThenDrainsOutsideTheWriteGate()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(['T'], Token);
        var original = index.Root('T').DriveBlock;
        var catchUp = index.WaitForCatchUpAsync(['T'], Token);
        var applying = harness.TrackGate();
        index.Hooks.ApplyJournalEntriesEntered = _ =>
        {
            applying.MarkEntered();
            applying.WaitForRelease();
        };
        var queued = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "stale.txt", 700));
        await applying.Entered.WaitAsync(HangGuard);
        index.Hooks.PublishInsideWriteGate = _ =>
        {
            Assert.AreSame(original, index.Root('T').DriveBlock);
            Assert.IsFalse(catchUp.IsCompleted, "retirement must wait until the locked commit");
        };
        harness.SetNextProducedCursor('T', 13, 9000);

        var rescan = index.RescanAsync(['T'], Token);

        Assert.AreNotSame(original, index.Root('T').DriveBlock, "publication precedes the pump drain");
        Assert.IsFalse(rescan.IsCompleted);
        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => catchUp);
        await index.WaitForDriveWriteGateForTest('T').WaitAsync(HangGuard);
        index.ReleaseDriveWriteGateForTest('T');
        applying.Release();
        await rescan.WaitAsync(HangGuard);
        await queued.WaitAsync(HangGuard);
        Assert.IsEmpty(harness.Changes);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.Source.TargetsFor('T')[1]);
        await index.StopWatchingAsync(['T'], Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposal_DuringProduction_EndsTheOriginalWatch(bool dispose)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');
        var production = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync(['T'], Token);
        await production.Entered.WaitAsync(HangGuard);
        Assert.AreEqual(0, handle.DisposeCount, "production keeps the original watch until stop or disposal");
        if (dispose)
        {
            var disposal = harness.Index.DisposeAsync().AsTask();
            await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => rescan);
            await disposal.WaitAsync(HangGuard);
        }
        else
        {
            await harness.Index.StopWatchingAsync(['T'], Token);
            production.Release();
            await rescan.WaitAsync(HangGuard);
            Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
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
        await harness.Index.StartWatchingAsync(['T'], Token);
        Task? disposal = null;
        void BeginDisposal(DriveKey _) => disposal = index.DisposeAsync().AsTask();
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
        var failure = await DriveResultAssertions.FailureAsync<OperationCanceledException>(
            () => harness.Index.RescanAsync(['T'], Token));
        Assert.AreSame(producerFailure, failure);
        Assert.IsNull(disposal, "a failed scan never enters a restart checkpoint");
        await harness.Index.DisposeAsync();
    }

    [TestMethod]
    public async Task FaultSettledBeforeCommit_QueuedAfterCommit_DoesNotRecoverTheReplacement()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await harness.Index.StartWatchingAsync(['T'], Token);
        var faultSettled = harness.TrackGate();
        harness.Index.Hooks.PumpFaultSettlementWrapper = settle =>
        {
            settle();
            faultSettled.MarkEntered();
            faultSettled.WaitForRelease();
        };
        var production = harness.HoldNextProduction('T');
        var original = harness.Index.Root('T').DriveBlock;
        var rescan = harness.Index.RescanAsync(['T'], Token);
        await production.Entered.WaitAsync(HangGuard);
        Assert.AreEqual(0, harness.Source.WatchFor('T').DisposeCount);
        harness.Source.WatchFor('T').FailDrive(new IOException("fault before commit"));
        await faultSettled.Entered.WaitAsync(HangGuard);
        var restart = harness.TrackGate();
        harness.Index.Hooks.BeforeRestartDecision = _ =>
        {
            restart.MarkEntered();
            restart.WaitForRelease();
        };
        WatchState? stateAtFault = null;
        index.WatchFaulted += _ => stateAtFault = index.Drives.Single().Watch.State;
        var publishing = harness.TrackGate();
        harness.Index.Hooks.PublishInsideWriteGate = _ => publishing.MarkEntered();
        production.Release();
        // Publication can be observed while the faulted pump is held before queuing recovery.
        await publishing.Entered.WaitAsync(HangGuard);
        await harness.Index.WaitForDriveWriteGateForTest('T');
        harness.Index.ReleaseDriveWriteGateForTest('T');
        faultSettled.Release();
        await restart.Entered.WaitAsync(HangGuard);
        Assert.AreNotSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(WatchState.Faulted, stateAtFault);
        restart.Release();
        await rescan.WaitAsync(HangGuard);
        Assert.AreEqual(2, harness.ProductionCount('T'));
        await harness.Index.StopWatchingAsync(['T'], Token);
    }
}
