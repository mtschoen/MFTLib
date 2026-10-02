using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

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
    static readonly TimeSpan HangGuard = FakeIndexWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StartWatching_OneDrive_StartsOnlyThatDrive()
    {
        using var harness = new WatchHarness();

        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);

        await harness.Index.StartWatchingAsync('T', Token);

        CollectionAssert.AreEqual(
            new[] { new IndexWatchTarget('T', WatchHarness.JournalId, WatchHarness.NextUsn) },
            harness.Source.Starts.ToArray());
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('U').WatchCatchUp);
    }

    [TestMethod]
    public async Task StartWatching_AlreadyWatching_DoesNotRestart()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);

        await harness.Index.StartWatchingAsync('T', Token);

        Assert.AreEqual(1, harness.Source.Starts.Count);
    }

    [TestMethod]
    public async Task StartWatching_SourceStartThrows_ThrowsAndFaultsSlot()
    {
        using var harness = new WatchHarness();
        var failure = new IOException("the broker is gone");
        harness.Source.FailStart(failure);

        var thrown = await ThrowsAsync<IOException>(() => harness.Index.StartWatchingAsync('T', Token));

        Assert.AreSame(failure, thrown);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.WatchCatchUp);
        Assert.AreEqual("the broker is gone", drive.WatchFailureMessage);
    }

    [TestMethod]
    public async Task StartWatching_AfterACancelledStart_StartsFresh()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var start = harness.Index.StartWatchingAsync('T', callerCancellation.Token);
        await held.Entered.WaitAsync(HangGuard);
        await callerCancellation.CancelAsync();
        await ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard));

        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        Assert.AreEqual(1, harness.Source.Handles.Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task StartWatching_AfterASourceStartFailure_StartsFresh()
    {
        using var harness = new WatchHarness();
        var failure = new IOException("the broker could not be reached");
        harness.Source.FailStart(failure);
        var thrown = await ThrowsAsync<IOException>(() => harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard));
        Assert.AreSame(failure, thrown);

        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        Assert.AreEqual(1, harness.Source.Handles.Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task StartWatching_AfterAStopDuringStart_StartsFresh()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        var start = harness.Index.StartWatchingAsync('T', Token);
        await held.Entered.WaitAsync(HangGuard);
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
        await ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard));
        Assert.AreEqual(0, harness.Source.Handles.Count, "the cancelled start returned no handle, so no pump ran");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);

        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        Assert.AreEqual(1, harness.Source.Handles.Count);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task DisposeDuringStart_CancelsTheSourceStart_AndCompletes()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        var start = harness.Index.StartWatchingAsync('T', Token);
        await held.Entered.WaitAsync(HangGuard);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await ThrowsAsync<OperationCanceledException>(() => start);
    }

    [TestMethod]
    public async Task StartReturnsAfterStop_HandleDisposedByStartPath()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held, observeToken: false);
        var start = harness.Index.StartWatchingAsync('T', Token);
        await held.Entered.WaitAsync(HangGuard);
        var stop = harness.Index.StopWatchingAsync('T', Token);

        held.Release();

        await stop.WaitAsync(HangGuard);
        await ThrowsAsync<OperationCanceledException>(() => start);
        var handle = harness.Source.Handles.Single();
        Assert.AreEqual(1, handle.DisposeCount);
        Assert.IsFalse(handle.ReadStarted, "an unpublished handle is never read");
        await harness.Index.DisposeAsync();
        Assert.AreEqual(1, handle.DisposeCount);
    }

    [TestMethod]
    public async Task Batch_AppliesAndRaisesChanged()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);

        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(9, "fresh.txt", nextUsn: 555));

        Assert.AreEqual(555L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);
        Assert.AreEqual(1, harness.Changes.Count);
        Assert.AreEqual(FileChangeKind.Created, harness.Changes.Single().Kind);
        Assert.AreEqual("fresh.txt", harness.Changes.Single().Entry.Name);
    }

    [TestMethod]
    public async Task DriveCaughtUp_CompletesWait()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);
        var wait = harness.Index.WaitForCatchUpAsync('T', Token);
        Assert.IsFalse(wait.IsCompleted, "nothing has caught up yet");

        await harness.Source.HandleFor('T').Publish(new DriveCaughtUp());

        await wait.WaitAsync(HangGuard);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task DriveFault_RaisesDriveKindAndFaultsOnlyThatDrive()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);

        harness.Source.HandleFor('T').FailDrive(new IOException("the volume went away"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.Source.HandleFor('U').Publish(WatchHarness.Batch(10, "u.txt"));

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual('T', ((DriveWatchFaultException)fault.Exception).DriveLetter);
        Assert.AreEqual(WatchCatchUpState.Recovering, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('U').WatchCatchUp);
        Assert.IsNull(harness.DriveFor('U').WatchFailureMessage);
        Assert.IsTrue(harness.Changes.Any(change => change.Entry.Name == "u.txt"), "U kept applying");
    }

    [TestMethod]
    public async Task ApplyFailure_RaisesApplyKind()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        var cursorBefore = harness.BlockFor('T').Header.UsnNextUsn;

        await harness.Source.HandleFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalId, NextUsn: 900));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');

        Assert.IsInstanceOfType<ArgumentNullException>(fault.Exception);
        Assert.AreEqual(cursorBefore, harness.BlockFor('T').Header.UsnNextUsn, "a rejected batch advances nothing");
        Assert.IsNotNull(harness.DriveFor('T').WatchFailureMessage);
    }

    [TestMethod]
    public async Task SubscriberThrows_AnnouncedOncePerWatch_DriveKeepsWatching()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("the subscriber blew up");
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');

        await handle.Publish(WatchHarness.Batch(9, "first.txt", nextUsn: 300));
        await handle.Publish(WatchHarness.Batch(10, "second.txt", nextUsn: 400));

        Assert.AreEqual(1, harness.Faults.Count(fault => fault.Kind == WatchFaultKind.Subscriber));
        Assert.AreEqual(2, harness.Changes.Count, "both batches were applied");
        Assert.AreEqual(400L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
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
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var tConsumed = harness.Source.HandleFor('T').Queue(WatchHarness.Batch(9, "t.txt"));
        await blocked.Entered.WaitAsync(HangGuard);

        await harness.Source.HandleFor('U').Publish(WatchHarness.Batch(10, "u.txt"));

        Assert.IsTrue(harness.Changes.Any(change => change.Entry.Name == "u.txt"));
        Assert.IsFalse(tConsumed.IsCompleted, "T's handler is still blocked");
        blocked.Release();
        await tConsumed.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task Stop_RethrowsOutstandingFaultOnce()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        harness.Source.HandleFor('T').FailDrive(new IOException("the volume went away"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var thrown = await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));

        Assert.AreSame(fault.Exception, thrown);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token),
            "the first stop cleared the request and retired the instance, so the drive is not watching");
    }

    [TestMethod]
    public async Task Stop_DriveNeverStarted_ThrowsInvalidOperation()
    {
        using var harness = new WatchHarness();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync('T', Token));

        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task CacheOnlyUnresumable_StartThrowsWithRescanMessage()
    {
        using var fixture = new CacheOnlyUnresumableFixture();
        await using var index = await fixture.OpenAdoptingAnUnresumableBlockAsync(Token);

        var thrown = await ThrowsAsync<InvalidOperationException>(() => index.StartWatchingAsync('T', Token));

        StringAssert.Contains(thrown.Message, "FileIndex.RescanAsync");
        var drive = index.Drives.Single();
        Assert.AreEqual(thrown.Message, drive.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.WatchCatchUp);
        Assert.AreEqual(0, fixture.Source.Starts.Count, "the source is never asked to start the drive");
    }
}
