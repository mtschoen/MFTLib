using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan whose watch session ends while the rescan is in flight: the last watched drive
///     faults, the pump stops reading, and the source releases its stream. The rescan must notice
///     that the session it disarmed on is over and start a fresh one rather than arming the
///     rescanned drive onto a stream that no longer exists. The scan is held inside the producer so
///     every ordering here is fixed by the test rather than by timing.
/// </summary>
[TestClass]
public class FileIndexWatchRescanEndedSessionTests
{
    static readonly IndexWatchTarget[] TwoDrives =
        [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)];

    static readonly IndexWatchTarget FreshCursorForT = new('T', 13, 9000);

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task RescanAsync_QueuedFromTheLastDrivesFaultHandler_WhenTheSessionEndsMidScan_StartsAFreshSession()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var production = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', FreshCursorForT.JournalId, FreshCursorForT.NextUsn);

        var index = harness.Index;
        var queuedRescan = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.DriveLetter == 'T' && !queuedRescan.Task.IsCompleted)
            {
                queuedRescan.TrySetResult(Task.Run(() => index.RescanAsync('T', Token)));
            }
        };

        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        var rescan = await queuedRescan.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
        await AwaitScanStartedAsync(production, rescan);

        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        production.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);

        await AssertRestartedWithOnlyTheRescannedDriveAsync(harness);
        await AssertStopRethrowsTheOtherDrivesFaultAsync(harness);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseDisarmFailsBecauseTheSessionAlreadyEnded_StartsAFreshSessionInsteadOfFailing()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var sourceEnding = harness.HoldSourceEnding();

        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));

        // The stream is released and the pump is still unwinding, so the session is over but its
        // pump has not completed: the disarm reaches a source with no stream.
        await sourceEnding.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        var production = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', FreshCursorForT.JournalId, FreshCursorForT.NextUsn);
        var rejected = harness.SourceRejectedAsync();
        var rescan = harness.Index.RescanAsync('T', Token);
        await rejected;

        sourceEnding.Release();
        await AwaitScanStartedAsync(production, rescan);
        Assert.AreEqual(0, harness.WatchOperations.Count, "the disarm found no stream to stop");
        production.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);

        await AssertRestartedWithOnlyTheRescannedDriveAsync(harness);
        await AssertStopRethrowsTheOtherDrivesFaultAsync(harness);
    }

    [TestMethod]
    public async Task RescanAsync_OfAFaultedDrive_WhenTheLastHealthyDriveFaultsMidScan_StartsAFreshSession()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));

        var production = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', FreshCursorForT.JournalId, FreshCursorForT.NextUsn);
        var rescan = harness.Index.RescanAsync('T', Token);
        await AwaitScanStartedAsync(production, rescan);
        CollectionAssert.AreEqual(new[] { "disarm:T" }, harness.WatchOperations.ToArray());

        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        production.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);

        await AssertRestartedWithOnlyTheRescannedDriveAsync(harness);
        await AssertStopRethrowsTheOtherDrivesFaultAsync(harness);
    }

    [TestMethod]
    public async Task RescanAsync_OfAHealthyDrive_WhenTheOtherDriveFaultsMidScan_ReArmsOnTheSameSession()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var production = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', FreshCursorForT.JournalId, FreshCursorForT.NextUsn);
        var rescan = harness.Index.RescanAsync('T', Token);
        await AwaitScanStartedAsync(production, rescan);

        // T was disarmed but carries no failure, so it still counts as watched and the pump keeps
        // reading after U drops.
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));

        production.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(1, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.AreEqual(FreshCursorForT, harness.ArmedDrives.Single());
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual("U's journal wrapped", DriveFor(harness, 'U').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 13, NextUsn: 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);
        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task RescanAsync_WhoseDisarmIsRejectedAfterTheStreamEndedButBeforeThePumpFinished_StartsAFreshSession()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));

        // The stream returns with U still healthy. The source releases it in its finally and is
        // parked there, so the pump has not yet reached its end-of-stream bookkeeping.
        var sourceEnding = harness.HoldSourceEnding();
        var sourceCompleted = harness.CompleteSourceAsync();
        await sourceEnding.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);

        harness.SetNextProducedCursor('T', FreshCursorForT.JournalId, FreshCursorForT.NextUsn);
        var rejected = harness.SourceRejectedAsync();
        var rescan = harness.Index.RescanAsync('T', Token);
        await rejected;

        sourceEnding.Release();
        await sourceCompleted;
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);

        var targets = await harness.SourceStartedAsync();
        Assert.AreEqual(2, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { FreshCursorForT, TwoDrives[1] }, targets.ToArray());
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_AfterASourceFaultEndedTheSession_StopStillRethrowsTheSourceFault()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "before.txt")], JournalId: 22, NextUsn: 8500));
        var sourceFault = new InvalidOperationException("the watch stream crashed");
        await harness.FaultSourceAsync(sourceFault);
        await harness.WaitForPumpToCompleteAsync();

        await harness.Index.RescanAsync('T', Token);
        await harness.SourceStartedAsync();
        Assert.AreEqual(2, harness.SourceInvocationCount);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(sourceFault, thrown);
    }

    [TestMethod]
    public async Task RescanAsync_AfterASubscriberFaultAndEveryDriveFaulted_StopStillRethrowsTheSubscriberFault()
    {
        using var harness = new WatchHarness(TwoDrives);
        var subscriberFault = new ArgumentException("the consumer's handler threw");
        harness.Index.Changed += _ => throw subscriberFault;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "before.txt")], JournalId: 22, NextUsn: 8500));
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        harness.SetNextProducedCursor('T', FreshCursorForT.JournalId, FreshCursorForT.NextUsn);
        await harness.Index.RescanAsync('T', Token);
        await AssertRestartedWithOnlyTheRescannedDriveAsync(harness);

        var thrown = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(subscriberFault, thrown);
    }

    [TestMethod]
    public async Task RescanAsync_AfterASourceFault_WhenTheReplacementStreamEndsCleanly_StopStillRethrowsTheSourceFault()
    {
        using var harness = new WatchHarness(TwoDrives);
        var index = harness.Index;
        var sessionEndAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault is { Kind: WatchFaultKind.Source, DriveLetter: null } &&
                fault.Exception.Message.Contains("ended its stream", StringComparison.Ordinal))
            {
                sessionEndAnnounced.TrySetResult();
            }
        };
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "before.txt")], JournalId: 22, NextUsn: 8500));
        var sourceFault = new InvalidOperationException("the watch stream crashed");
        await harness.FaultSourceAsync(sourceFault);
        await harness.WaitForPumpToCompleteAsync();

        await harness.Index.RescanAsync('T', Token);
        await harness.SourceStartedAsync();

        // The replacement stream ends with both drives still watched and no fault of its own,
        // so the pump reports the end and releases the session.
        await harness.CompleteSourceAsync();
        await sessionEndAnnounced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(sourceFault, thrown);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_OfTheOtherDriveAfterARestart_RecoversItsCarriedFault()
    {
        using var harness = new WatchHarness(TwoDrives);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        await harness.Index.RescanAsync('T', Token);
        await harness.SourceStartedAsync();
        await harness.Index.RescanAsync('U', Token);

        CollectionAssert.AreEqual(new[] { "arm:U" }, harness.WatchOperations.ToArray());
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);
        await harness.Index.StopWatchingAsync(Token);
    }

    /// <summary>
    ///     The restart recovered only T, so U's own fault from the ended session is still
    ///     outstanding and is what the stop rethrows.
    /// </summary>
    async Task AssertStopRethrowsTheOtherDrivesFaultAsync(WatchHarness harness)
    {
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("U's journal wrapped", thrown.Message);
    }

    internal static DriveStatus DriveFor(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(driveLetter));
    }

    /// <summary>
    ///     Waits until the held scan has started, which proves the rescan got past its suspend
    ///     step. A rescan that fails before scanning finishes first instead, and awaiting it
    ///     surfaces that failure as the test's own.
    /// </summary>
    static async Task AwaitScanStartedAsync(TestGate production, Task rescan)
    {
        var first = await Task.WhenAny(production.Entered, rescan).WaitAsync(FakeIndexWatchSource.HangGuard);
        if (first == rescan)
        {
            await rescan;
            Assert.Fail("The rescan completed without starting its scan.");
        }
    }

    /// <summary>
    ///     The outcome every ended-session ordering shares: a second source stream carrying only
    ///     the rescanned drive from its fresh cursor, the rescanned drive's failure cleared, and
    ///     the other drive's failure left exactly as its own fault recorded it.
    /// </summary>
    static async Task AssertRestartedWithOnlyTheRescannedDriveAsync(WatchHarness harness)
    {
        var targets = await harness.SourceStartedAsync();
        Assert.AreEqual(2, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { FreshCursorForT }, targets.ToArray());

        var rescanned = DriveFor(harness, 'T');
        Assert.IsNull(rescanned.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, rescanned.WatchCatchUp);

        var untouched = DriveFor(harness, 'U');
        Assert.AreEqual("U's journal wrapped", untouched.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, untouched.WatchCatchUp);
    }
}
