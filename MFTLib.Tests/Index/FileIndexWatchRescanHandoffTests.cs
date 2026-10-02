using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanHandoffTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task AppliedChange_DeliveredAfterCommit_CanRepeatDuringReplacementCatchUp()
    {
        var directory = Directory.CreateTempSubdirectory("mftlib-rescan-events-");
        var delivering = new TestGate();
        try
        {
            var source = new FakeIndexWatchSource();
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', directory.FullName, 1)],
                CacheDirectory = directory.FullName,
                ProducerPolicy = ProducerPolicy.Mft,
                WatchSource = source,
                MftProducer = (request, cancellationToken) =>
                {
                    MftBlockFixture.Write(request.BlockPath, 1, WatchHarness.JournalId, 100, moment: MftBlockFixture.SeededMoment);
                    var block = BlockFile.Open(request.BlockPath, 1, out _)!;
                    return Task.FromResult(new MftBlockProduceResult(block, WatchHarness.JournalId, 100, 0));
                }
            }, Token);
            var changes = new System.Collections.Concurrent.ConcurrentQueue<FileChange>();
            index.Changed += changes.Enqueue;
            await index.StartWatchingAsync('T', Token);
            index.BeforeWatchChangedForTest = _ =>
            {
                delivering.MarkEntered();
                delivering.WaitForRelease();
            };
            try
            {
                var delivered = source.HandleFor('T').Queue(WatchHarness.Batch(9, "replayed.txt", 700));
                await delivering.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
                var original = index.Root('T').DriveBlock;
                var rescan = index.RescanAsync('T', Token);
                Assert.AreNotSame(original, index.Root('T').DriveBlock,
                    "publication need not wait for an already applied change's delivery");
                Assert.AreEqual(0, changes.Count);
                Assert.AreEqual(0, index.FindByName("replayed.txt", Token).Count, "queries await the new catch-up");
                delivering.Release();
                await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);
                await delivered.WaitAsync(FakeIndexWatchSource.HangGuard);
                Assert.AreEqual(1, changes.Count);
                await source.HandleFor('T').Publish(WatchHarness.Batch(9, "replayed.txt", 700));
                Assert.AreEqual(2, changes.Count);
                Assert.AreEqual(WatchCatchUpState.CatchingUp, index.Drives.Single().WatchCatchUp);
                await source.HandleFor('T').Publish(new DriveCaughtUp());
                await index.WaitForCatchUpAsync('T', Token);
                Assert.AreEqual(1, index.FindByName("replayed.txt", Token).Count);
                await index.StopWatchingAsync('T', Token);
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
        await index.StartWatchingAsync('T', Token);
        var original = index.Root('T').DriveBlock;
        var catchUp = index.WaitForCatchUpAsync('T', Token);
        var applying = harness.TrackGate();
        index.ApplyJournalEntriesEnteredForTest = _ =>
        {
            applying.MarkEntered();
            applying.WaitForRelease();
        };
        var queued = harness.Source.HandleFor('T').Queue(WatchHarness.Batch(9, "stale.txt", 700));
        await applying.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        index.PublishInsideWriteGateForTest = _ =>
        {
            Assert.AreSame(original, index.Root('T').DriveBlock);
            Assert.IsFalse(catchUp.IsCompleted, "retirement must wait until the locked commit");
        };
        harness.SetNextProducedCursor('T', 13, 9000);

        var rescan = index.RescanAsync('T', Token);

        Assert.AreNotSame(original, index.Root('T').DriveBlock, "publication precedes the pump drain");
        Assert.IsFalse(rescan.IsCompleted);
        await FileIndexWatchRescanTests.ThrowsAsync<OperationCanceledException>(() => catchUp);
        await index.WaitForDriveWriteGateForTest('T').WaitAsync(FakeIndexWatchSource.HangGuard);
        index.ReleaseDriveWriteGateForTest('T');
        applying.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);
        await queued.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(0, harness.Changes.Count);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.Source.StartsFor('T')[1]);
        await index.StopWatchingAsync('T', Token);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposal_DuringProduction_EndsTheOriginalWatch(bool dispose)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        var production = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(0, handle.DisposeCount, "production keeps the original watch until stop or disposal");
        if (dispose)
        {
            var disposal = harness.Index.DisposeAsync().AsTask();
            await FileIndexWatchRescanTests.ThrowsAsync<OperationCanceledException>(() => rescan);
            await disposal.WaitAsync(FakeIndexWatchSource.HangGuard);
        }
        else
        {
            await harness.Index.StopWatchingAsync('T', Token);
            production.Release();
            await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);
            Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
        }

        Assert.AreEqual(1, handle.DisposeCount);
        Assert.AreEqual(1, harness.Source.StartsFor('T').Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedScan_HasNoRestartCheckpointAtWhichDisposalCanReplaceItsFailure(bool afterDecision)
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await harness.Index.StartWatchingAsync('T', Token);
        Task? disposal = null;
        void BeginDisposal(char _) => disposal = index.DisposeAsync().AsTask();
        if (afterDecision)
        {
            harness.Index.RestartRequestedForTest = BeginDisposal;
        }
        else
        {
            harness.Index.BeforeRestartDecisionForTest = BeginDisposal;
        }

        var failure = new OperationCanceledException("production failed");
        harness.FailNextProduction('T', failure);
        var thrown = await FileIndexWatchRescanTests.ThrowsAsync<OperationCanceledException>(
            () => harness.Index.RescanAsync('T', Token));
        Assert.AreSame(failure, thrown);
        Assert.IsNull(disposal, "a failed scan never enters a restart checkpoint");
        await harness.Index.DisposeAsync();
    }

    [TestMethod]
    public async Task FaultSettledBeforeCommit_QueuedAfterCommit_DoesNotRecoverTheReplacement()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await harness.Index.StartWatchingAsync('T', Token);
        var faultSettled = harness.TrackGate();
        harness.Index.PumpFaultSettlementWrapperForTest = settle =>
        {
            settle();
            faultSettled.MarkEntered();
            faultSettled.WaitForRelease();
        };
        var production = harness.HoldNextProduction('T');
        var original = harness.Index.Root('T').DriveBlock;
        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(0, harness.Source.HandleFor('T').DisposeCount);
        harness.Source.HandleFor('T').FailDrive(new IOException("fault before commit"));
        await faultSettled.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        var restart = harness.TrackGate();
        harness.Index.BeforeRestartDecisionForTest = _ =>
        {
            restart.MarkEntered();
            restart.WaitForRelease();
        };
        WatchCatchUpState? stateAtFault = null;
        index.WatchFaulted += _ => stateAtFault = index.Drives.Single().WatchCatchUp;
        var publishing = harness.TrackGate();
        harness.Index.PublishInsideWriteGateForTest = _ => publishing.MarkEntered();
        production.Release();
        // Publication can be observed while the faulted pump is held before queuing recovery.
        await publishing.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        await harness.Index.WaitForDriveWriteGateForTest('T');
        harness.Index.ReleaseDriveWriteGateForTest('T');
        faultSettled.Release();
        await restart.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreNotSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(WatchCatchUpState.Faulted, stateAtFault);
        restart.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(2, harness.ProductionCount('T'));
        await harness.Index.StopWatchingAsync('T', Token);
    }
}
