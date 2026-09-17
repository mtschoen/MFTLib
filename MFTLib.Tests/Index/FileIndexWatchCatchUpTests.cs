using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchCatchUpTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static DriveStatus DriveFor(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(
            drive => char.ToUpperInvariant(drive.DriveLetter) == char.ToUpperInvariant(driveLetter));
    }

    [TestMethod]
    public void WatchCatchUp_IsNotStartedBeforeTheWatchStarts()
    {
        using var harness = new WatchHarness();

        Assert.AreEqual(WatchCatchUpState.NotStarted, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task StartWatchingAsync_BeginsCatchingUpEveryWatchedDrive()
    {
        using var harness = new WatchHarness();

        await harness.Index.StartWatchingAsync(Token);

        Assert.AreEqual(WatchCatchUpState.CatchingUp, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task DriveCaughtUpItem_FlipsTheDriveToCaughtUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);

        await harness.PublishAsync(new DriveCaughtUp('T'));

        Assert.AreEqual(WatchCatchUpState.CaughtUp, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task DriveWatchFailure_FaultsTheDrivesCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);

        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("journal wrapped")));

        Assert.AreEqual(WatchCatchUpState.Faulted, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task DriveCaughtUpItem_ForAFaultedDriveIsIgnored()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("journal wrapped")));

        await harness.PublishAsync(new DriveCaughtUp('T'));

        Assert.AreEqual(WatchCatchUpState.Faulted, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task StopWatchingAsync_ResetsCatchUpToNotStarted()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));

        await harness.Index.StopWatchingAsync(Token);

        Assert.AreEqual(WatchCatchUpState.NotStarted, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task SourceCompletingWithoutAStop_FaultsEveryDrivesCatchUp()
    {
        using var harness = new WatchHarness();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.WatchFaulted += _ => announced.TrySetResult();
        await harness.Index.StartWatchingAsync(Token);

        await harness.CompleteSourceAsync();
        await announced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(WatchCatchUpState.Faulted, DriveFor(harness, 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task RescanAsync_ReArmResetsTheDriveToCatchingUp()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveCaughtUp('T'));
        Assert.AreEqual(WatchCatchUpState.CaughtUp, DriveFor(harness, 'T').WatchCatchUp);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(WatchCatchUpState.CatchingUp, DriveFor(harness, 'T').WatchCatchUp);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, DriveFor(harness, 'U').WatchCatchUp);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CompletesOnlyAfterTheBacklogIsApplied()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync('T', Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "new.txt"));
        Assert.IsFalse(wait.IsCompleted);

        await harness.PublishAsync(new DriveCaughtUp('T'));
        await wait.WaitAsync(FakeIndexWatchSource.HangGuard);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CompletesImmediatelyWhenAlreadyCaughtUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));

        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        Assert.IsTrue(wait.IsCompleted);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_WaitsForTheSlowestDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));
        Assert.IsFalse(wait.IsCompleted);

        await harness.PublishAsync(new DriveCaughtUp('U'));
        await wait.WaitAsync(FakeIndexWatchSource.HangGuard);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_FaultsWhenTheDrivesWatchFaults()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        var failure = new IOException("journal wrapped");
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        await harness.PublishAsync(new DriveWatchFailure('T', failure));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_OnAnAlreadyFaultedDrive_ReturnsAFaultedTask()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        var failure = new IOException("journal wrapped");
        await harness.PublishAsync(new DriveWatchFailure('T', failure));

        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AfterARescanReArm_CompletesAfterTheReArmedCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);
        Assert.IsFalse(wait.IsCompleted);

        await harness.PublishAsync(new DriveCaughtUp('T'));
        await wait.WaitAsync(FakeIndexWatchSource.HangGuard);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_PendingAcrossARescanReArm_IsCancelledWithTheSupersededArm()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        await harness.Index.RescanAsync('T', Token);

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_DisposingTheIndexCancelsAPendingWait()
    {
        var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        await harness.Index.DisposeAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
        harness.Dispose();
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_CallerCancellationEndsTheWaitButNotTheCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        using var caller = new CancellationTokenSource();
        var wait = harness.Index.WaitForCatchUpAsync('T', caller.Token);

        await caller.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, DriveFor(harness, 'T').WatchCatchUp);

        await harness.PublishAsync(new DriveCaughtUp('T'));
        await harness.Index.WaitForCatchUpAsync('T', Token).WaitAsync(FakeIndexWatchSource.HangGuard);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_ThrowsWhenTheDriveIsNotWatched()
    {
        using var harness = new WatchHarness();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.WaitForCatchUpAsync('T', Token));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_ThrowsForADriveThatIsNotInTheIndex()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => harness.Index.WaitForCatchUpAsync('X', Token));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_ThrowsWhenNoWatchIsRunning()
    {
        using var harness = new WatchHarness();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.WaitForCatchUpAsync(Token));
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_SourceException_FaultsPendingWait()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        var failure = new IOException("stream died");
        await harness.FaultSourceAsync(failure);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_SessionCancellation_CancelsPendingWait()
    {
        using var harness = new WatchHarness();
        using var sessionCts = new CancellationTokenSource();
        await harness.Index.StartWatchingAsync(sessionCts.Token);
        await harness.SourceStartedAsync();
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);

        await sessionCts.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_DriveFaultsAfterCatchUp_ReturnsFaultedTask()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));

        var caughtUpWait = harness.Index.WaitForCatchUpAsync('T', Token);
        Assert.IsTrue(caughtUpWait.IsCompletedSuccessfully);

        var failure = new IOException("journal wrapped");
        await harness.PublishAsync(new DriveWatchFailure('T', failure));

        var singleWait = harness.Index.WaitForCatchUpAsync('T', Token);
        var thrownSingle = await Assert.ThrowsExceptionAsync<IOException>(() => singleWait);
        Assert.AreSame(failure, thrownSingle);

        var allWait = harness.Index.WaitForCatchUpAsync(Token);
        var thrownAll = await Assert.ThrowsExceptionAsync<IOException>(() => allWait);
        Assert.AreSame(failure, thrownAll);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_FaultsImmediatelyWhenFirstDriveFaultsWhileSecondIsCatchingUp()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        var failure = new IOException("drive T failed");
        await harness.PublishAsync(new DriveWatchFailure('T', failure));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUpWhileOtherDriveIsCatchingUp()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));
        Assert.IsFalse(wait.IsCompleted);

        var failure = new IOException("drive T failed after catchup");
        await harness.PublishAsync(new DriveWatchFailure('T', failure));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveReArmedAfterCatchUpWhileOtherDriveIsCatchingUp_CancelsAggregateWait()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));
        Assert.IsFalse(wait.IsCompleted);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, DriveFor(harness, 'T').WatchCatchUp);

        await harness.PublishAsync(new DriveCaughtUp('U'));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUpThenOtherDriveCatchesUp_FaultsAggregateWait()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        await harness.PublishAsync(new DriveCaughtUp('T'));
        Assert.IsFalse(wait.IsCompleted);

        var failure = new IOException("drive T failed after catchup");
        await harness.PublishAsync(new DriveWatchFailure('T', failure));
        await harness.PublishAsync(new DriveCaughtUp('U'));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveFaultsWhileCatchingUpThenOtherDriveCatchesUp_FaultsAggregateWait()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        var failure = new IOException("drive T failed while catching up");
        await harness.PublishAsync(new DriveWatchFailure('T', failure));
        await harness.PublishAsync(new DriveCaughtUp('U'));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => wait);
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_DriveReArmedWhileCatchingUpThenOtherDriveCatchesUp_CancelsAggregateWait()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync(Token);
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);
        await harness.PublishAsync(new DriveCaughtUp('U'));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
    }
}
