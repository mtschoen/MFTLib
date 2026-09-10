using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The recovery contract: a rescan while a watch is running touches exactly one drive, and a
///     rescan after every drive has faulted reclaims the session and starts a fresh one.
/// </summary>
[TestClass]
public class FileIndexWatchRescanTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task RescanAsync_WhileWatching_ReArmsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("journal wrapped")));
        Assert.IsNotNull(DriveFor(harness, 'T').WatchFailureMessage);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(1, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.ArmedDrives.Single());
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 13, NextUsn: 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_NeverStopsTheOtherDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var changes = new List<FileChange>();
        harness.Index.Changed += changes.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "before.txt")], JournalId: 22, NextUsn: 8500));
        Assert.AreEqual(8500L, harness.BlockFor('U').Header.UsnNextUsn);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "after.txt")], JournalId: 22, NextUsn: 8600));

        CollectionAssert.DoesNotContain(harness.DisarmedDrives.ToArray(), 'U');
        CollectionAssert.DoesNotContain(harness.ArmedDrives.Select(target => target.DriveLetter).ToArray(), 'U');
        Assert.AreEqual(8600L, harness.BlockFor('U').Header.UsnNextUsn);
        CollectionAssert.AreEqual(new[] { "before.txt", "after.txt" },
            changes.Select(change => change.Entry.Name).ToArray());
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_DropsABatchAlreadyQueuedOnTheMergedStreamWhenTheDriveWasDisarmed()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        harness.HoldItemsUnread();
        var queued = harness.Queue(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "stale.txt")], JournalId: 11, NextUsn: 5000));

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        harness.ReleaseHeldItems();
        await queued.WaitAsync(FakeIndexWatchSource.HangGuard);

        // The wire arm epoch drops what the broker had already written; this drops what the
        // source had already queued, which is the one no wire rule can reach.
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalId);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_AfterEveryDriveFaulted_ReclaimsTheSessionAndStartsAFreshOne()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        await harness.Index.RescanAsync('T', Token);
        await harness.SourceStartedAsync();

        Assert.AreEqual(2, harness.SourceInvocationCount);
        Assert.AreEqual(0, harness.WatchOperations.Count, "A dead session is restarted whole, not re-armed.");
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WithNoWatchRunning_ArmsNothingAndDisarmsNothing()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(0, harness.SourceInvocationCount);
        Assert.AreEqual(0, harness.ArmedDrives.Count);
        Assert.AreEqual(0, harness.DisarmedDrives.Count);
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalId);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseSwapFails_ReArmsTheDriveFromItsUnchangedCursorAndRethrows()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var scanFailure = new OperationCanceledException("the scan was cancelled");
        harness.FailNextProduction('T', scanFailure);
        var thrown = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => harness.Index.RescanAsync('T', Token));

        Assert.AreSame(scanFailure, thrown);

        // The block was never swapped, so the drive resumes from the cursor its header still
        // carries. A failed rescan must not leave the drive disarmed and silently unwatched.
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.AreEqual(new IndexWatchTarget('T', 11, 4242), harness.ArmedDrives.Single());
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 11, NextUsn: 5000));
        Assert.AreEqual(5000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseSwapAndReArmBothFail_AnnouncesTheFreezeAndLeavesTheOtherDriveStreaming()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var scanFailure = new OperationCanceledException("the scan was cancelled");
        harness.FailNextProduction('T', scanFailure);
        harness.FailNextArm(new IOException("the source could not resume this drive"));
        var thrown = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => harness.Index.RescanAsync('T', Token));

        // The freeze is made visible rather than left silent, and the original failure is what
        // names it: the re-arm failure is only why the drive could not be put back.
        CollectionAssert.Contains(thrown.InnerExceptions.ToArray(), scanFailure);
        Assert.AreEqual("the scan was cancelled", DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual(DriveState.Ready, DriveFor(harness, 'T').State);
        var driveFault = faults.Single(fault => fault.DriveLetter == 'T');
        Assert.AreEqual(WatchFaultKind.Source, driveFault.Kind);
        Assert.AreSame(scanFailure, driveFault.Exception);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);

        // The rescan already threw this failure to its own caller, so the pump never observed it
        // and the stop has nothing to rethrow.
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseDisarmFails_AnnouncesTheStoppedDriveAndLeavesTheOtherStreaming()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var disarmFailure = new IOException("the source could not stop this drive");
        harness.FailNextDisarm(disarmFailure);
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.RescanAsync('T', Token));

        // A disarm that throws has already taken the drive off the watch, so a healthy status and
        // no fault would be exactly the silent stop the swap path is written to prevent.
        Assert.AreSame(disarmFailure, thrown);
        Assert.AreEqual("the source could not stop this drive", DriveFor(harness, 'T').WatchFailureMessage);
        var driveFault = faults.Single(fault => fault.DriveLetter == 'T');
        Assert.AreEqual(WatchFaultKind.Source, driveFault.Kind);
        Assert.AreSame(disarmFailure, driveFault.Exception);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseReArmFailsAfterTheSwapSucceeded_AnnouncesTheStoppedDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        var armFailure = new IOException("the source could not resume this drive");
        harness.FailNextArm(armFailure);
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.RescanAsync('T', Token));

        // The swap succeeded, so the block is current and only the watch is missing. Clearing the
        // failure before arming is what would otherwise leave the drive reading healthy.
        Assert.AreSame(armFailure, thrown);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual("the source could not resume this drive", DriveFor(harness, 'T').WatchFailureMessage);
        var driveFault = faults.Single(fault => fault.DriveLetter == 'T');
        Assert.AreEqual(WatchFaultKind.Source, driveFault.Kind);
        Assert.AreSame(armFailure, driveFault.Exception);
        await harness.Index.StopWatchingAsync(Token);
    }

    static DriveStatus DriveFor(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(driveLetter));
    }
}
