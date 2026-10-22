using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.WaitForCatchUpAsync" /> over the configured drive keys: the all-drives
///     catch-up cases of the merged-stream design, restated for the per-drive contract. The wait
///     names every drive, returns after each one has settled, and reports one result per drive: a
///     drive that caught up is <see cref="DriveOperationOutcome.Succeeded" /> for good (a later
///     fault or rescan does not undo what was awaited), a drive whose watch faulted or was
///     superseded before it caught up is <see cref="DriveOperationOutcome.Failed" /> without ending
///     the wait for the others, and a drive that is not watching is
///     <see cref="DriveOperationOutcome.NotApplicable" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexBatchedWaitTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_WaitsForTheSlowestDrive()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.IsFalse(wait.IsCompleted);

        await harness.Source.WatchFor('U').Publish(new DriveCaughtUp());
        var results = await wait.WaitAsync(HangGuard);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_WithASingleWatchedDrive_CompletesWithItsCatchUp()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        Assert.IsFalse(wait.IsCompleted);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        var results = await wait.WaitAsync(HangGuard);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results.Single().Outcome);
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_WhenEveryDriveAlreadyCaughtUp_IsCompleteAtIssue()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await harness.Source.WatchFor('U').Publish(new DriveCaughtUp());

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);

        Assert.IsTrue(wait.IsCompletedSuccessfully);
        Assert.IsTrue((await wait).All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_WhenNoWatchIsRunning_ReportsEveryDriveNotApplicable()
    {
        using var harness = new WatchHarness('T', 'U');

        var results = await harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        Assert.AreSequenceEqual(['T', 'U'], [.. results.Select(result => result.Drive.Letter)]);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.NotApplicable));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_WhenFirstDriveFaultsWhileSecondIsCatchingUp_ReportsItFailedAfterTheSecondSettles()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        var failure = new IOException("drive T failed");
        harness.Source.WatchFor('T').FailDrive(failure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.IsFalse(wait.IsCompleted, "the call returns only after every drive has settled");

        await harness.Source.WatchFor('U').Publish(new DriveCaughtUp());
        var results = await wait.WaitAsync(HangGuard);
        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome);
        Assert.IsInstanceOfType<DriveWatchFaultException>(results[0].Failure);
        Assert.AreSame(failure, results[0].Failure!.InnerException);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[1].Outcome);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUpWhileOtherDriveIsCatchingUp_StillReportsItSucceeded()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.IsFalse(wait.IsCompleted);

        harness.Source.WatchFor('T').FailDrive(new IOException("drive T failed after catchup"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.IsFalse(wait.IsCompleted, "U is still catching up");

        await harness.Source.WatchFor('U').Publish(new DriveCaughtUp());
        var results = await wait.WaitAsync(HangGuard);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveReArmedAfterCatchUpWhileOtherDriveIsCatchingUp_StillReportsItSucceeded()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.IsFalse(wait.IsCompleted);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.Index.RescanAsync(['T'], Token).WaitAsync(HangGuard);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);

        await harness.Source.WatchFor('U').Publish(new DriveCaughtUp());
        var results = await wait.WaitAsync(HangGuard);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveReArmedWhileCatchingUpThenOtherDriveCatchesUp_ReportsTheFirstCancelled()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.Index.RescanAsync(['T'], Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('U').Publish(new DriveCaughtUp());

        var results = await wait.WaitAsync(HangGuard);
        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome);
        Assert.IsInstanceOfType<OperationCanceledException>(results[0].Failure);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[1].Outcome);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUp_ReportsItFailedToALaterWait()
    {
        using var harness = new WatchHarness('T');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        var failure = new IOException("journal wrapped");
        harness.Source.WatchFor('T').FailDrive(failure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var results = await harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);

        Assert.AreEqual(DriveOperationOutcome.Failed, results.Single().Outcome);
        Assert.AreSame(failure, results.Single().Failure!.InnerException);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_IndexDisposedWhilePending_IsCancelled()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);
        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_CancelledToken_ThrowsAfterEveryDriveSettles()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var wait = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), cancellation.Token);

        await cancellation.CancelAsync().WaitAsync(HangGuard);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(HangGuard));
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State,
            "cancelling the wait leaves the drives' watches alone");
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('U').Watch.State);
    }
}
