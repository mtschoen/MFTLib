using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class FileIndexWatchFailedRescanTests
{
    static readonly IndexWatchTarget[] TwoDrives =
        [new('T', 11, 4242), new('U', 22, 8484)];

    public TestContext TestContext { get; set; } = null!;
    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static DriveStatus DriveFor(WatchHarness harness, char letter) =>
        harness.Index.Drives.Single(drive => drive.DriveLetter == letter);

    static IDisposable LostCheckpointForT() =>
        JournalCheckpointCheck.OverrideJournalForTest(letter => letter == 'T'
            ? new JournalWindow(11, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);

    static void AssertStillFaulted(WatchHarness harness, DriveStatus before,
        DriveBlock original, int sourceCount, int operationOffset)
    {
        var after = DriveFor(harness, 'T');
        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual(before.WatchFailureMessage, after.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, after.WatchCatchUp);
        Assert.AreEqual(before.CheckpointLoss, after.CheckpointLoss);
        Assert.AreEqual(sourceCount, harness.SourceInvocationCount);
        CollectionAssert.DoesNotContain(
            harness.WatchOperations.Skip(operationOffset).ToArray(), "arm:T");
    }

    [DataTestMethod]
    [DataRow("running", false)]
    [DataRow("ended-before", false)]
    [DataRow("ended-during", false)]
    [DataRow("retained-ledger", false)]
    [DataRow("running", true)]
    [DataRow("ended-before", true)]
    [DataRow("ended-during", true)]
    [DataRow("retained-ledger", true)]
    public async Task FailedRescan_PreservesFaultAndDoesNotRecover(string shape, bool thrownSwap)
    {
        using var harness = new WatchHarness(TwoDrives);
        using var journal = LostCheckpointForT();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var watchFailure = new IOException("T lost its checkpoint");
        await harness.PublishAsync(new DriveWatchFailure('T', watchFailure));

        if (shape is "ended-before" or "retained-ledger")
        {
            await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U stopped")));
            await harness.SourceEndedAsync();
            await harness.WaitForPumpToCompleteAsync();
        }
        if (shape == "retained-ledger")
        {
            await harness.Index.RescanAsync('U', Token);
            var targets = await harness.SourceStartedAsync();
            Assert.AreEqual('U', targets.Single().DriveLetter);
        }

        var before = DriveFor(harness, 'T');
        Assert.IsNotNull(before.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, before.CheckpointLoss.DetectedDuring);
        var original = harness.Index.Root('T').DriveBlock;
        var sourceCount = harness.SourceInvocationCount;
        var operationOffset = harness.WatchOperations.Count;
        Exception productionFailure = thrownSwap
            ? new OperationCanceledException("producer aborted before replacement")
            : new IOException("rescan producer failed");
        harness.FailNextProduction('T', productionFailure);
        var production = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        if (shape == "ended-during")
        {
            await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U stopped")));
            await harness.SourceEndedAsync();
            await harness.WaitForPumpToCompleteAsync();
        }
        production.Release();
        if (thrownSwap)
        {
            var thrown = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => rescan.WaitAsync(FakeIndexWatchSource.HangGuard));
            Assert.AreSame(productionFailure, thrown);
        }
        else
        {
            await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);
            Assert.AreEqual(productionFailure.Message, DriveFor(harness, 'T').MftProducerFailureMessage);
        }

        AssertStillFaulted(harness, before, original, sourceCount, operationOffset);
        if (shape is "running" or "retained-ledger")
        {
            await harness.PublishAsync(new JournalBatch('U',
                [WatchHarness.Create(9, "sibling.txt")], JournalId: 22, NextUsn: 9000));
            Assert.AreEqual(9000L, harness.Index.Root('U').DriveBlock.Block.Header.UsnNextUsn);
            Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);
        }

        var stopFailure = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(watchFailure, stopFailure);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledPublication_PreservesFaultAndDisposesUnpublishedBlock(bool ended)
    {
        using var harness = new WatchHarness(TwoDrives);
        using var journal = LostCheckpointForT();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var watchFailure = new IOException("T lost its checkpoint");
        await harness.PublishAsync(new DriveWatchFailure('T', watchFailure));
        if (ended)
        {
            await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U stopped")));
            await harness.SourceEndedAsync();
            await harness.WaitForPumpToCompleteAsync();
        }

        var before = DriveFor(harness, 'T');
        var original = harness.Index.Root('T').DriveBlock;
        var sourceCount = harness.SourceInvocationCount;
        var operationOffset = harness.WatchOperations.Count;
        var field = typeof(FileIndex).GetField("_swapGate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var swapGate = (SemaphoreSlim)field.GetValue(harness.Index)!;
        await swapGate.WaitAsync(Token);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var produced = harness.ObserveNextProducedBlock('T');
            var rescan = harness.Index.RescanAsync('T', cancellation.Token);
            var unpublished = await produced.WaitAsync(FakeIndexWatchSource.HangGuard);
            cancellation.Cancel();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => rescan.WaitAsync(FakeIndexWatchSource.HangGuard));
            Assert.ThrowsException<ObjectDisposedException>(() => _ = unpublished.Header);
            AssertStillFaulted(harness, before, original, sourceCount, operationOffset);
        }
        finally
        {
            swapGate.Release();
        }

        var stopFailure = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(watchFailure, stopFailure);
    }

    [TestMethod]
    public async Task HealthyDrive_ProducerFailure_RestoresOldWatch()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var original = harness.Index.Root('T').DriveBlock;
        harness.FailNextProduction('T', new IOException("rescan producer failed"));
        await harness.Index.RescanAsync('T', Token);

        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual("rescan producer failed", DriveFor(harness, 'T').MftProducerFailureMessage);
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual(1, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.AreEqual(TwoDrives[0], harness.ArmedDrives.Single());
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(9, "still-watching.txt")], JournalId: 11, NextUsn: 4500));
        Assert.AreEqual(4500L, harness.Index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulRetry_AfterFailedProduction_ReplacesAndCatchesUp(bool ended)
    {
        using var harness = new WatchHarness(ended ? [TwoDrives[0]] : TwoDrives);
        using var journal = LostCheckpointForT();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T lost its checkpoint")));
        if (ended)
        {
            await harness.SourceEndedAsync();
            await harness.WaitForPumpToCompleteAsync();
        }
        var before = DriveFor(harness, 'T');
        var original = harness.Index.Root('T').DriveBlock;
        harness.FailNextProduction('T', new IOException("first attempt failed"));
        await harness.Index.RescanAsync('T', Token);
        AssertStillFaulted(harness, before, original, sourceCount: 1, operationOffset: 0);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);
        if (ended)
        {
            var targets = await harness.SourceStartedAsync();
            CollectionAssert.AreEqual(new[] { new IndexWatchTarget('T', 13, 9000) }, targets.ToArray());
        }
        else
        {
            Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.ArmedDrives.Single());
        }
        Assert.AreEqual(ended ? 2 : 1, harness.SourceInvocationCount);
        Assert.AreNotSame(original, harness.Index.Root('T').DriveBlock);
        Assert.IsNull(DriveFor(harness, 'T').MftProducerFailureMessage);
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.IsNull(DriveFor(harness, 'T').CheckpointLoss);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, DriveFor(harness, 'T').WatchCatchUp);
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(9, "recovered.txt")], JournalId: 13, NextUsn: 9500));
        await harness.PublishAsync(new DriveCaughtUp('T'));
        await harness.Index.WaitForCatchUpAsync('T', Token);
        Assert.AreEqual(9500L, harness.Index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, DriveFor(harness, 'T').WatchCatchUp);
        await harness.Index.StopWatchingAsync(Token);
    }
}
