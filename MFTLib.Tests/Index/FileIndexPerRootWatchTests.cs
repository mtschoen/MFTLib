using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Each root's watch runs through its own handle, pump, and catch-up slot: starting,
///     stopping, faulting, or rescanning one root acts on that root's records only. The
///     linearization rules (R1: a retiring pump never reaches its successor; R2: a stop or
///     disposal during a start cancels the source's start; R4: a rescan holds the root's
///     lifecycle gate through production) are pinned with <see cref="TestGate" /> holds rather
///     than timing. Ordered across files: this one covers start, stop, the pump and faults;
///     <c>FileIndexPerDriveWatchTests.Lifecycle.cs</c> covers retirement, rescan, disposal,
///     handle ownership, and catch-up waits.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexPerRootWatchTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task StartWatching_OneRoot_StartsOnlyThatRoot()
    {
        using var harness = new WatchHarness();

        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);

        await harness.IndexFor('T').StartWatchingAsync(Token);

        Assert.AreSequenceEqual(
            [new IndexWatchTarget(new VolumeKey('T'), WatchHarness.JournalIdentifier, WatchHarness.NextUsn)], [.. harness.Source.Targets]);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('U').Watch.State);
    }

    [TestMethod]
    public async Task StartWatching_AlreadyWatching_DoesNotRestart()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);

        await harness.IndexFor('T').StartWatchingAsync(Token);

        Assert.HasCount(1, harness.Source.Starts);
    }

    [TestMethod]
    public async Task StartWatching_SourceStartThrows_ReportsFailedResultAndFaultsSlot()
    {
        using var harness = new WatchHarness();
        var sourceFailure = new IOException("the broker is gone");
        harness.Source.FailNextStart(sourceFailure);

        var failure = await RootResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('T').StartWatchingAsync(Token));

        Assert.AreSame(sourceFailure, failure);
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.AreEqual("the broker is gone", root.Watch.FailureMessage);
    }

    [TestMethod]
    public async Task StartWatching_AfterACancelledStart_StartsFresh()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var start = harness.IndexFor('T').StartWatchingAsync(callerCancellation.Token);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await callerCancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard, TestContext.CancellationToken));

        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.HasCount(1, harness.Source.Watches);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatching_AfterASourceStartFailure_StartsFresh()
    {
        using var harness = new WatchHarness();
        var sourceFailure = new IOException("the broker could not be reached");
        harness.Source.FailNextStart(sourceFailure);
        var failure = await RootResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(sourceFailure, failure);

        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.HasCount(1, harness.Source.Watches);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatching_AfterAStopDuringStart_StartsFresh()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        var start = harness.IndexFor('T').StartWatchingAsync(Token);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await RootResultAssertions.FailureAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.IsEmpty(harness.Source.Watches, "the cancelled start returned no handle, so no pump ran");
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);

        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.HasCount(1, harness.Source.Watches);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task DisposeDuringStart_CancelsTheSourceStart_AndCompletes()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        var start = harness.IndexFor('T').StartWatchingAsync(Token);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        await RootResultAssertions.FailureAsync<OperationCanceledException>(() => start);
    }

    [TestMethod]
    public async Task StartReturnsAfterStop_HandleDisposedByStartPath()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held, observeToken: false);
        var start = harness.IndexFor('T').StartWatchingAsync(Token);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var stop = harness.IndexFor('T').StopWatchingAsync(Token);

        held.Release();

        await stop.WaitAsync(HangGuard, TestContext.CancellationToken);
        await RootResultAssertions.FailureAsync<OperationCanceledException>(() => start);
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
        await harness.IndexFor('T').StartWatchingAsync(Token);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "fresh.txt", nextUsn: 555));

        Assert.AreEqual(555L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        Assert.HasCount(1, harness.Changes);
        Assert.AreEqual(FileChangeKind.Created, harness.Changes.Single().Kind);
        Assert.AreEqual("fresh.txt", harness.Changes.Single().Entry.Name);
    }

    [TestMethod]
    public async Task RootCaughtUp_CompletesWait()
    {
        using var harness = new WatchHarness();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var wait = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        Assert.IsFalse(wait.IsCompleted, "nothing has caught up yet");

        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());

        await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task RootFault_RaisesRootKindAndFaultsOnlyThatRoot()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);

        harness.Source.WatchFor('T').FailVolume(new IOException("the volume went away"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt"));

        Assert.HasCount(1, harness.Faults);
        Assert.AreEqual('T', fault.Volume);
        Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('U').Watch.State);
        Assert.IsNull(harness.RootFor('U').Watch.FailureMessage);
        Assert.Contains(change => change.Entry.Name == "u.txt", harness.Changes, "U kept applying");
    }

    [TestMethod]
    public async Task ApplyFailure_RaisesApplyKind()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var cursorBefore = harness.BlockFor('T').Header.UsnNextUsn;

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, NextUsn: 900));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');

        Assert.IsInstanceOfType<ArgumentNullException>(fault.Exception);
        Assert.AreEqual(cursorBefore, harness.BlockFor('T').Header.UsnNextUsn, "a rejected batch advances nothing");
        Assert.IsNotNull(harness.RootFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task SubscriberThrows_AnnouncedOncePerWatch_RootKeepsWatching()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("the subscriber blew up");
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');

        await handle.Publish(WatchHarness.Batch(9, "first.txt", nextUsn: 300));
        await handle.Publish(WatchHarness.Batch(10, "second.txt", nextUsn: 400));

        Assert.ContainsSingle(fault => fault.Kind == WatchFaultKind.Subscriber, harness.Faults);
        Assert.HasCount(2, harness.Changes, "both batches were applied");
        Assert.AreEqual(400L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
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
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var tConsumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "t.txt"));
        await blocked.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt"));

        Assert.Contains(change => change.Entry.Name == "u.txt", harness.Changes);
        Assert.IsFalse(tConsumed.IsCompleted, "T's handler is still blocked");
        blocked.Release();
        await tConsumed.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Stop_ReportsOutstandingFaultOnceAsFailedResult()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailVolume(new IOException("the volume went away"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');

        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));

        Assert.AreSame(fault.Exception, failure);
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
        await RootResultAssertions.NotApplicableAsync(
            () => harness.IndexFor('T').StopWatchingAsync(Token),
            "the first stop cleared the request and retired the instance, so the root is not watching");
    }

    [TestMethod]
    public async Task Stop_RootNeverStarted_ReportsNotApplicable()
    {
        using var harness = new WatchHarness();

        await RootResultAssertions.NotApplicableAsync(() => harness.IndexFor('T').StopWatchingAsync(Token));

        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task CacheOnlyUnresumable_StartReportsFailedResultWithRescanMessage()
    {
        using var fixture = new CheckpointLossFixture(seedDocuments: false);
        var source = new ScriptedWatchSource();
        await using var index = await fixture.OpenAdoptingAnUnresumableBlockAsync(source, Token);

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => index.StartWatchingAsync(Token));

        Assert.Contains("FileIndex.RescanAsync", failure.Message);
        var root = index.Status;
        Assert.AreEqual(failure.Message, root.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.IsEmpty(source.Starts, "the source is never asked to start the root");
    }
}
