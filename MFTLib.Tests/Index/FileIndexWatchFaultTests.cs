using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The per-drive fault lane. Every fault here is injected through the fake source or a
///     throwing subscriber, so none of these needs a broker, a volume, or Windows.
/// </summary>
[TestClass]
public class FileIndexWatchFaultTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task ApplyFailure_IsReportedAsAnApplyFaultAndIsolatesTheDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        var changes = new List<FileChange>();
        harness.Index.Changed += changes.Add;

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var frozenCursor = harness.BlockFor('T').Header.UsnNextUsn;
        await harness.PublishAsync(new JournalBatch('T', null!, JournalId: 11, NextUsn: 5000));

        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Apply, faults[0].Kind);
        Assert.AreEqual('T', faults[0].DriveLetter);
        Assert.IsInstanceOfType<ArgumentNullException>(faults[0].Exception);
        Assert.IsNotNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual(DriveState.Ready, DriveFor(harness, 'T').State);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "later.txt")], JournalId: 11, NextUsn: 6000));
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));

        Assert.AreEqual(frozenCursor, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual("u.txt", changes[0].Entry.Name);

        var thrown = await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(faults[0].Exception, thrown);
    }

    [TestMethod]
    public async Task ApplyFailure_OnADriveWithNoBlock_IsAnnouncedWithoutADriveStatusMessage()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('Q', [], JournalId: 7, NextUsn: 200));

        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Apply, faults[0].Kind);
        Assert.AreEqual('Q', faults[0].DriveLetter);
        Assert.IsInstanceOfType<ArgumentException>(faults[0].Exception);
        Assert.IsTrue(harness.Index.Drives.All(drive => drive.WatchFailureMessage is null));

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "one.txt")], JournalId: 7, NextUsn: 300));
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task ApplyFailure_OnADriveWithNoBlock_IsAnnouncedOnceHoweverOftenItRepeats()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('Q', [], JournalId: 7, NextUsn: 200));
        await harness.PublishAsync(new JournalBatch('Q', [], JournalId: 7, NextUsn: 250));

        // With no ordinal to key, the ordinal dictionary cannot deduplicate this drive at all.
        // Only the pump's session-local set of dropped letters can, which is what this pins.
        Assert.AreEqual(1, faults.Count);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "one.txt")], JournalId: 7, NextUsn: 300));
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task DriveWatchFailure_IsReportedAsASourceFaultCarryingTheDriveLetter()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        var wrapped = new IOException("journal wrapped");

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var frozenCursor = harness.BlockFor('T').Header.UsnNextUsn;
        await harness.PublishAsync(new DriveWatchFailure('T', wrapped));

        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Source, faults[0].Kind);
        Assert.AreEqual('T', faults[0].DriveLetter);
        Assert.AreSame(wrapped, faults[0].Exception);
        Assert.AreEqual("journal wrapped", DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual(DriveState.Ready, DriveFor(harness, 'T').State);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "later.txt")], JournalId: 11, NextUsn: 6000));
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));

        Assert.AreEqual(frozenCursor, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(wrapped, thrown);
    }

    [TestMethod]
    public async Task DriveWatchFailure_RepeatedForTheSameDrive_IsAnnouncedOnlyOnce()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        var first = new IOException("first failure");
        await harness.PublishAsync(new DriveWatchFailure('T', first));
        // With an ordinal to key, the failure-message dictionary is what must deduplicate
        // this, unlike the no-block drive case above which falls back to the pump's own set.
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("second failure")));

        Assert.AreEqual(1, faults.Count);
        Assert.AreSame(first, faults[0].Exception);
        Assert.AreEqual("first failure", DriveFor(harness, 'T').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(first, thrown);
    }

    [TestMethod]
    public async Task WholeStreamFault_EndsThePumpAndNamesNoDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.FaultSourceAsync(new IOException("the broker died"));

        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Source, faults[0].Kind);
        Assert.IsNull(faults[0].DriveLetter);
        Assert.IsTrue(harness.Index.Drives.All(drive => drive.WatchFailureMessage is null));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("the broker died", thrown.Message);
    }

    [TestMethod]
    public async Task EveryDriveFaulted_EndsThePumpAndLeavesTheFirstFaultForStop()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var driveTFailure = new IOException("T's journal wrapped");

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', driveTFailure));
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        // The pump broke out of its await-foreach rather than being cancelled, which is what
        // ends the watch at the source without a stop having been asked for.
        Assert.IsFalse(harness.SourceCancelled);
        foreach (var driveLetter in new[] { 'T', 'U' })
        {
            Assert.IsNotNull(DriveFor(harness, driveLetter).WatchFailureMessage);
            Assert.AreEqual(DriveState.Ready, DriveFor(harness, driveLetter).State);
        }

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(driveTFailure, thrown);
    }

    [TestMethod]
    public async Task SubscriberFault_DoesNotDropTheDrive()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        var changeCount = 0;
        harness.Index.Changed += _ =>
        {
            changeCount++;
            if (changeCount == 1)
            {
                throw new InvalidOperationException("subscriber failed");
            }
        };

        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "one.txt")], JournalId: 7, NextUsn: 200));
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 10, "two.txt")], JournalId: 7, NextUsn: 300));

        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task CallerTokenCancellation_EndsTheWatchWithNoFault()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        using var callerCancellation = new CancellationTokenSource();

        await harness.Index.StartWatchingAsync(callerCancellation.Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "one.txt")], JournalId: 7, NextUsn: 200));
        await callerCancellation.CancelAsync();
        await harness.SourceEndedAsync();

        Assert.IsTrue(harness.SourceCancelled);
        Assert.AreEqual(0, faults.Count);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_WithACancelledToken_AbandonsTheWaitAndLeavesTheSessionReclaimable()
    {
        using var harness = new WatchHarness();
        harness.IgnoreSourceCancellation();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        using var cancelledStop = new CancellationTokenSource();
        await cancelledStop.CancelAsync();
        var abandoned = await CatchAsync(() => harness.Index.StopWatchingAsync(cancelledStop.Token));
        Assert.IsInstanceOfType<OperationCanceledException>(abandoned);

        harness.ReleaseWedgedSource();
        await harness.SourceEndedAsync();
        await harness.Index.StopWatchingAsync(CancellationToken.None);
        await harness.Index.StartWatchingAsync(Token);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartAfterStop_LeavesNoOrphanedSessionAndClearsEveryWatchFailure()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("journal wrapped")));
        Assert.IsNotNull(DriveFor(harness, 'T').WatchFailureMessage);
        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));

        await harness.Index.StartWatchingAsync(Token);
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 7, NextUsn: 300));
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);

        // The loop form rather than a deterministic interleaving: StartWatchingAsync publishes the
        // session under the state lock and there is no seam to suspend it between the claim and
        // the assignment, so no single ordering can be forced. The loop catches an orphan on any
        // interleaving that leaves one behind.
        for (var iteration = 0; iteration < 20; iteration++)
        {
            await harness.Index.StartWatchingAsync(Token);
            await harness.Index.StopWatchingAsync(Token);
            Assert.AreEqual(0, harness.LiveSourceCount, $"Iteration {iteration} left a source live.");
        }
    }

    /// <summary>
    ///     Catches by assignability rather than by exact type, which is what separates the
    ///     abandoned wait's contract (any <see cref="OperationCanceledException" />) from the
    ///     particular subclass a given await happens to raise.
    /// </summary>
    static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    static DriveStatus DriveFor(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(driveLetter));
    }
}
