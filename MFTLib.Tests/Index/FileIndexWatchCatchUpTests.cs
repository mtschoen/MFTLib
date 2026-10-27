using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchCatchUpTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task DriveFailure_FaultsTheDrivesCatchUp()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);

        harness.Source.WatchFor('T').FailDrive(new IOException("journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        Assert.AreEqual(WatchState.Recovering, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task StopWatchingAsync_ResetsCatchUpToNotStarted()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        await harness.IndexFor('T').StopWatchingAsync(Token);

        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task RescanAsync_ReArmResetsTheDriveToCatchingUp()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);

        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('U').Watch.State);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CompletesOnlyAfterTheBacklogIsApplied()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');

        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "new.txt"));
        Assert.IsFalse(wait.IsCompleted);

        await handle.Publish(new DriveCaughtUp());
        await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CompletesImmediatelyWhenAlreadyCaughtUp()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        Assert.IsTrue(wait.IsCompleted);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_ReportsFailureWhenTheDrivesWatchFaults()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var watchFailure = new IOException("journal wrapped");
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        harness.Source.WatchFor('T').FailDrive(watchFailure);

        var failure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => wait);
        Assert.AreSame(watchFailure, failure.InnerException);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_OnAnAlreadyFaultedDrive_ReturnsFailedResult()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var watchFailure = new IOException("journal wrapped");
        harness.Source.WatchFor('T').FailDrive(watchFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        var failure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => wait);
        Assert.AreSame(watchFailure, failure.InnerException);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AfterARescanReArm_CompletesAfterTheReArmedCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        Assert.IsFalse(wait.IsCompleted);

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_PendingAcrossARescanReArm_IsCancelledWithTheSupersededArm()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        await harness.IndexFor('T').RescanAsync(Token);

        await DriveResultAssertions.FailureAsync<TaskCanceledException>(() => wait);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_DisposingTheIndexCancelsAPendingWait()
    {
        var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        await harness.Index.DisposeAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => wait);
        harness.Dispose();
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CallerCancellationEndsTheWaitButNotTheCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        using var caller = new CancellationTokenSource();
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(caller.Token);

        await caller.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => wait);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);

        var replacementWait = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        Assert.IsFalse(replacementWait.IsCompleted, "the replacement wait remains pending until the drive catches up");
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await replacementWait.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_ReportsNotApplicableWhenTheDriveIsNotWatched()
    {
        using var harness = new WatchHarness();

        await DriveResultAssertions.NotApplicableAsync(
            () => harness.IndexFor('T').WaitForCatchUpAsync(Token));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_ChannelLoss_SettlesPendingWaitWithFailedResult()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);

        var channelFailure = new IOException("stream died");
        harness.Source.WatchFor('T').LoseChannel(channelFailure);

        var failure = await DriveResultAssertions.FailureAsync<IOException>(() => wait);
        Assert.AreSame(channelFailure, failure);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_DriveFaultsAfterCatchUp_ReturnsFailedResult()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        var caughtUpWait = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        Assert.IsTrue(caughtUpWait.IsCompletedSuccessfully);

        var watchFailure = new IOException("journal wrapped");
        harness.Source.WatchFor('T').FailDrive(watchFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var singleWait = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        var failure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => singleWait);
        Assert.AreSame(watchFailure, failure.InnerException);
    }

    [TestMethod]
    public async Task DriveCaughtUpItem_Repeated_LeavesTheDriveCaughtUpWithoutAFault()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(new DriveCaughtUp());

        await handle.Publish(new DriveCaughtUp());

        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
        Assert.IsEmpty(harness.Faults);
        await harness.IndexFor('T').WaitForCatchUpAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_TokenAlreadyCancelled_ReturnsACancelledTaskAndLeavesTheWatch()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var wait = harness.IndexFor('T').WaitForCatchUpAsync(cancellation.Token);

        Assert.IsTrue(wait.IsCanceled);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
    }
}
