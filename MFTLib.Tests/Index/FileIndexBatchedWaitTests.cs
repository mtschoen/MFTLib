using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.WaitForCatchUpAsync" /> over a single root: a watch that caught up
///     is <see cref="DriveOperationOutcome.Succeeded" />, a watch that faulted or was superseded
///     before it caught up is <see cref="DriveOperationOutcome.Failed" />, and a root that is not
///     watching is <see cref="DriveOperationOutcome.NotApplicable" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexBatchedWaitTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task WaitForCatchUpAsync_WithWatchedDrive_CompletesWithItsCatchUp()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        Assert.IsFalse(wait.IsCompleted);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        var result = await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_WhenAlreadyCaughtUp_IsCompleteAtIssue()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        var wait = harness.Index.WaitForCatchUpAsync(Token);

        Assert.IsTrue(wait.IsCompletedSuccessfully);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, (await wait).Outcome);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_WhenNoWatchIsRunning_ReportsNotApplicable()
    {
        using var harness = new WatchHarness('T');

        var result = await harness.Index.WaitForCatchUpAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(new VolumeKey('T'), result.Drive);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, result.Outcome);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_WhenDriveFaultsWhileCatchingUp_ReportsFailed()
    {
        using var harness = new WatchHarness('T');
        harness.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        var failure = new IOException("drive T failed");
        harness.Source.WatchFor('T').FailDrive(failure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var result = await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(DriveOperationOutcome.Failed, result.Outcome);
        Assert.IsInstanceOfType<DriveWatchFaultException>(result.Failure);
        Assert.AreSame(failure, result.Failure!.InnerException);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_DriveReArmedWhileCatchingUp_ReportsCancelledResult()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.Index.RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        var result = await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(DriveOperationOutcome.Failed, result.Outcome);
        Assert.IsInstanceOfType<OperationCanceledException>(result.Failure);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_DriveFaultsAfterCatchUp_ReportsFailedToALaterWait()
    {
        using var harness = new WatchHarness('T');
        harness.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        var failure = new IOException("journal wrapped");
        harness.Source.WatchFor('T').FailDrive(failure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var result = await harness.Index.WaitForCatchUpAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(DriveOperationOutcome.Failed, result.Outcome);
        Assert.AreSame(failure, result.Failure!.InnerException);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_IndexDisposedWhilePending_IsCancelled()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var wait = harness.Index.WaitForCatchUpAsync(Token);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CancelledToken_ThrowsAndLeavesWatchAlone()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var wait = harness.Index.WaitForCatchUpAsync(cancellation.Token);

        await cancellation.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State,
            "cancelling the wait leaves the drive's watch alone");
    }
}
