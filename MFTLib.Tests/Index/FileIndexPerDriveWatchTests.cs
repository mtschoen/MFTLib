using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Each drive's watch runs through its own handle, pump, and catch-up slot: starting,
///     stopping, faulting, or rescanning one drive acts on that drive's records only. The
///     linearization rules (R1: a retiring pump never reaches its successor; R2: a stop or
///     disposal during a start cancels the source's start; R4: a rescan holds the drive's
///     lifecycle gate through production) are pinned with <see cref="TestGate" /> holds rather
///     than timing. Ordered across files: this one covers start, stop, the pump and faults;
///     <c>FileIndexPerDriveWatchTests.Lifecycle.cs</c> covers retirement, rescan, disposal,
///     handle ownership, and catch-up waits.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexPerDriveWatchTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StartWatching_OneDrive_StartsOnlyThatDrive()
    {
        using var harness = new WatchHarness();

        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);

        await harness.Index.StartWatchingAsync(['T'], Token);

        CollectionAssert.AreEqual(
            new[] { new IndexWatchTarget('T', WatchHarness.JournalIdentifier, WatchHarness.NextUsn) },
            harness.Source.Targets.ToArray());
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('U').Watch.State);
    }

    [TestMethod]
    public async Task StartWatching_AlreadyWatching_DoesNotRestart()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(['T'], Token);

        await harness.Index.StartWatchingAsync(['T'], Token);

        Assert.AreEqual(1, harness.Source.Starts.Count);
    }

    [TestMethod]
    public async Task StartWatching_SourceStartThrows_ReportsFailedResultAndFaultsSlot()
    {
        using var harness = new WatchHarness();
        var sourceFailure = new IOException("the broker is gone");
        harness.Source.FailNextStart(sourceFailure);

        var failure = await DriveResultAssertions.FailureAsync<IOException>(() => harness.Index.StartWatchingAsync(['T'], Token));

        Assert.AreSame(sourceFailure, failure);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchState.Faulted, drive.Watch.State);
        Assert.AreEqual("the broker is gone", drive.Watch.FailureMessage);
    }

    [TestMethod]
    public async Task StartWatching_AfterACancelledStart_StartsFresh()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var start = harness.Index.StartWatchingAsync(['T'], callerCancellation.Token);
        await held.Entered.WaitAsync(HangGuard);
        await callerCancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard));

        await harness.Index.StartWatchingAsync(['T'], Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(1, harness.Source.Watches.Count);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.Index.StopWatchingAsync(['T'], Token);
    }

    [TestMethod]
    public async Task StartWatching_AfterASourceStartFailure_StartsFresh()
    {
        using var harness = new WatchHarness();
        var sourceFailure = new IOException("the broker could not be reached");
        harness.Source.FailNextStart(sourceFailure);
        var failure = await DriveResultAssertions.FailureAsync<IOException>(() => harness.Index.StartWatchingAsync(['T'], Token).WaitAsync(HangGuard));
        Assert.AreSame(sourceFailure, failure);

        await harness.Index.StartWatchingAsync(['T'], Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(1, harness.Source.Watches.Count);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.Index.StopWatchingAsync(['T'], Token);
    }

    [TestMethod]
    public async Task StartWatching_AfterAStopDuringStart_StartsFresh()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        var start = harness.Index.StartWatchingAsync(['T'], Token);
        await held.Entered.WaitAsync(HangGuard);
        await harness.Index.StopWatchingAsync(['T'], Token).WaitAsync(HangGuard);
        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard));
        Assert.AreEqual(0, harness.Source.Watches.Count, "the cancelled start returned no handle, so no pump ran");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);

        await harness.Index.StartWatchingAsync(['T'], Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(1, harness.Source.Watches.Count);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        await harness.Index.StopWatchingAsync(['T'], Token);
    }

    [TestMethod]
    public async Task DisposeDuringStart_CancelsTheSourceStart_AndCompletes()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        var start = harness.Index.StartWatchingAsync(['T'], Token);
        await held.Entered.WaitAsync(HangGuard);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => start);
    }

    [TestMethod]
    public async Task StartReturnsAfterStop_HandleDisposedByStartPath()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held, observeToken: false);
        var start = harness.Index.StartWatchingAsync(['T'], Token);
        await held.Entered.WaitAsync(HangGuard);
        var stop = harness.Index.StopWatchingAsync(['T'], Token);

        held.Release();

        await stop.WaitAsync(HangGuard);
        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => start);
        var handle = harness.Source.Watches.Single();
        Assert.AreEqual(1, handle.DisposeCount);
        Assert.IsFalse(handle.ReadStarted, "an unpublished handle is never read");
        await harness.Index.DisposeAsync();
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task Batch_AppliesAndRaisesChanged()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(['T'], Token);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "fresh.txt", nextUsn: 555));

        Assert.AreEqual(555L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(['T'], Token);
        Assert.AreEqual(1, harness.Changes.Count);
        Assert.AreEqual(FileChangeKind.Created, harness.Changes.Single().Kind);
        Assert.AreEqual("fresh.txt", harness.Changes.Single().Entry.Name);
    }

    [TestMethod]
    public async Task DriveCaughtUp_CompletesWait()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(['T'], Token);
        var wait = harness.Index.WaitForCatchUpAsync(['T'], Token);
        Assert.IsFalse(wait.IsCompleted, "nothing has caught up yet");

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        await wait.WaitAsync(HangGuard);
        Assert.AreEqual(WatchState.CaughtUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task DriveFault_RaisesDriveKindAndFaultsOnlyThatDrive()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Index.StartWatchingAsync(['U'], Token);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt"));

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual('T', fault.Drive);
        Assert.AreEqual(WatchState.Recovering, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('U').Watch.State);
        Assert.IsNull(harness.DriveFor('U').Watch.FailureMessage);
        Assert.IsTrue(harness.Changes.Any(change => change.Entry.Name == "u.txt"), "U kept applying");
    }

    [TestMethod]
    public async Task ApplyFailure_RaisesApplyKind()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        var cursorBefore = harness.BlockFor('T').Header.UsnNextUsn;

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, NextUsn: 900));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');

        Assert.IsInstanceOfType<ArgumentNullException>(fault.Exception);
        Assert.AreEqual(cursorBefore, harness.BlockFor('T').Header.UsnNextUsn, "a rejected batch advances nothing");
        Assert.IsNotNull(harness.DriveFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task SubscriberThrows_AnnouncedOncePerWatch_DriveKeepsWatching()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("the subscriber blew up");
        await harness.Index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');

        await handle.Publish(WatchHarness.Batch(9, "first.txt", nextUsn: 300));
        await handle.Publish(WatchHarness.Batch(10, "second.txt", nextUsn: 400));

        Assert.AreEqual(1, harness.Faults.Count(fault => fault.Kind == WatchFaultKind.Subscriber));
        Assert.AreEqual(2, harness.Changes.Count, "both batches were applied");
        Assert.AreEqual(400L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task BlockedChangedHandlerOnT_DoesNotDelayU()
    {
        using var harness = new WatchHarness();
        var blocked = harness.TrackGate();
        harness.Index.Changed += change =>
        {
            if (change.Entry.Name == "t.txt")
            {
                blocked.MarkEntered();
                blocked.WaitForRelease();
            }
        };
        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Index.StartWatchingAsync(['U'], Token);
        var tConsumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt"));
        await blocked.Entered.WaitAsync(HangGuard);

        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt"));

        Assert.IsTrue(harness.Changes.Any(change => change.Entry.Name == "u.txt"));
        Assert.IsFalse(tConsumed.IsCompleted, "T's handler is still blocked");
        blocked.Release();
        await tConsumed.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task Stop_ReportsOutstandingFaultOnceAsFailedResult()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var failure = await DriveResultAssertions.FailureAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync(['T'], Token));

        Assert.AreSame(fault.Exception, failure);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.NotApplicableAsync(
            () => harness.Index.StopWatchingAsync(['T'], Token),
            "the first stop cleared the request and retired the instance, so the drive is not watching");
    }

    [TestMethod]
    public async Task Stop_DriveNeverStarted_ReportsNotApplicable()
    {
        using var harness = new WatchHarness();

        await DriveResultAssertions.NotApplicableAsync(() => harness.Index.StopWatchingAsync(['T'], Token));

        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task CacheOnlyUnresumable_StartReportsFailedResultWithRescanMessage()
    {
        using var fixture = new CheckpointLossFixture(seedDocuments: false);
        var source = new ScriptedWatchSource();
        await using var index = await fixture.OpenAdoptingAnUnresumableBlockAsync(source, Token);

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => index.StartWatchingAsync(['T'], Token));

        StringAssert.Contains(failure.Message, "FileIndex.RescanAsync");
        var drive = index.Drives.Single();
        Assert.AreEqual(failure.Message, drive.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, drive.Watch.State);
        Assert.AreEqual(0, source.Starts.Count, "the source is never asked to start the drive");
    }
}
