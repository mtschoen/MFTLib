using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan whose scan produces no replacement leaves a drive as its watch left it: a healthy
///     drive keeps its running watch, a faulted drive stays faulted with its fault, block and
///     checkpoint-loss report intact. The journal read is swapped out through
///     <c>JournalIsolation.OverrideJournalWindow</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexWatchFailedRescanTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    /// <summary>T's cursor has been trimmed out of its journal.</summary>
    static IDisposable LostCheckpointForT() =>
        JournalIsolation.OverrideJournalWindow(letter => letter == 'T'
            ? new SyntheticJournalWindow(WatchHarness.JournalIdentifier, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);

    static void AssertStillFaulted(WatchHarness harness, DriveStatus before, DriveBlock original, int startCount)
    {
        var after = harness.DriveFor('T');
        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual(before.Watch.FailureMessage, after.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, after.Watch.State);
        Assert.AreEqual(before.Watch.CheckpointLoss, after.Watch.CheckpointLoss);
        Assert.HasCount(startCount, harness.Source.TargetsFor('T'), "the condemned cursor is never started again");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedRescan_PreservesFaultAndDoesNotRecover(bool thrownSwap)
    {
        using var journal = LostCheckpointForT();
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Index.StartWatchingAsync(['U'], Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("T lost its checkpoint"));
        var watchFault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var before = harness.DriveFor('T');
        Assert.IsNotNull(before.Watch.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, before.Watch.CheckpointLoss.DetectedDuring);
        var original = harness.Index.Root('T', TestContext.CancellationToken).DriveBlock;
        var startCount = harness.Source.TargetsFor('T').Count;
        Exception productionFailure = thrownSwap
            ? new OperationCanceledException("producer aborted before replacement")
            : new IOException("rescan producer failed");
        harness.FailNextProduction('T', productionFailure);
        var production = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync(['T'], Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        production.Release();
        if (thrownSwap)
        {
            var failure = await DriveResultAssertions.FailureAsync<OperationCanceledException>(
                () => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.AreSame(productionFailure, failure);
        }
        else
        {
            var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.AreSame(productionFailure, failure.InnerException);
            Assert.AreEqual(productionFailure.Message, harness.DriveFor('T').Failure?.Message);
        }

        AssertStillFaulted(harness, before, original, startCount);
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(9, "sibling.txt", nextUsn: 900));
        Assert.AreEqual(900L, harness.Index.Root('U', TestContext.CancellationToken).DriveBlock.Block.Header.UsnNextUsn);
        Assert.IsNull(harness.DriveFor('U').Watch.FailureMessage);

        var stopFailure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));
        Assert.AreSame(watchFault.Exception, stopFailure);
        await harness.Index.StopWatchingAsync(['U'], Token);
    }

    [TestMethod]
    public async Task CancelledPublication_PreservesFaultAndDisposesUnpublishedBlock()
    {
        using var journal = LostCheckpointForT();
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("T lost its checkpoint"));
        var watchFault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var before = harness.DriveFor('T');
        var original = harness.Index.Root('T', TestContext.CancellationToken).DriveBlock;
        var startCount = harness.Source.TargetsFor('T').Count;
        var initialProducedBlock = harness.BlockFor('T');
        await harness.Index.WaitForDriveWriteGateForTest('T', TestContext.CancellationToken);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var rescan = harness.Index.RescanAsync(['T'], cancellation.Token);
            var unpublished = await FileIndexWatchRescanTests.WaitForReplacementBlockAsync(
                harness, 'T', initialProducedBlock, rescan);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = unpublished.Header);
            AssertStillFaulted(harness, before, original, startCount);
        }
        finally
        {
            harness.Index.ReleaseDriveWriteGateForTest('T');
        }

        var stopFailure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));
        Assert.AreSame(watchFault.Exception, stopFailure);
    }

    [TestMethod]
    public async Task HealthyDrive_ProducerFailure_KeepsOldWatch()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Index.StartWatchingAsync(['U'], Token);
        var original = harness.Index.Root('T', TestContext.CancellationToken).DriveBlock;
        var firstHandle = harness.Source.WatchFor('T');
        var producerFailure = new IOException("rescan producer failed");
        harness.FailNextProduction('T', producerFailure);

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.Index.RescanAsync(['T'], Token));

        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.AreSame(original, harness.Index.Root('T', TestContext.CancellationToken).DriveBlock);
        Assert.AreEqual("rescan producer failed", harness.DriveFor('T').Failure?.Message);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(0, firstHandle.DisposeCount, "the original watch is still running");
        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(1, starts);
        Assert.AreEqual(new IndexWatchTarget('T', WatchHarness.JournalIdentifier, WatchHarness.NextUsn), starts[0]);
        Assert.HasCount(1, harness.Source.TargetsFor('U'));
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "still-watching.txt", nextUsn: 4500));
        Assert.AreEqual(4500L, harness.Index.Root('T', TestContext.CancellationToken).DriveBlock.Block.Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(['T'], Token);
        await harness.Index.StopWatchingAsync(['U'], Token);
    }

    [TestMethod]
    public async Task SuccessfulRetry_AfterFailedProduction_ReplacesAndCatchesUp()
    {
        using var journal = LostCheckpointForT();
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("T lost its checkpoint"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        var before = harness.DriveFor('T');
        var original = harness.Index.Root('T', TestContext.CancellationToken).DriveBlock;
        harness.FailNextProduction('T', new IOException("first attempt failed"));
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.Index.RescanAsync(['T'], Token));
        AssertStillFaulted(harness, before, original, startCount: 1);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.Index.RescanAsync(['T'], Token);

        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(2, starts);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), starts[1]);
        Assert.AreNotSame(original, harness.Index.Root('T', TestContext.CancellationToken).DriveBlock);
        var drive = harness.DriveFor('T');
        Assert.IsNull(drive.Failure);
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.IsNull(drive.Watch.CheckpointLoss);
        Assert.AreEqual(WatchState.CatchingUp, drive.Watch.State);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(new JournalBatch([WatchHarness.Create(9, "recovered.txt")], 13, 9500));
        var caughtUp = harness.Index.WaitForCatchUpAsync(['T'], Token);
        await handle.Publish(new DriveCaughtUp());
        await caughtUp.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(9500L, harness.Index.Root('T', TestContext.CancellationToken).DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
        await harness.Index.StopWatchingAsync(['T'], Token);
    }
}
