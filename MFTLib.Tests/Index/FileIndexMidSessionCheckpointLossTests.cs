using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a consumer sees when a live watch dies because the journal has moved past the
///     position that watch was reading from. The drive's block header cursor is the watch
///     position, so the same <see cref="JournalCheckpointCheck" /> that decides a warm start
///     answers here too, and the answer is a fact about the journal rather than about the
///     exception that ended the watch. The journal read is swapped out through
///     <c>JournalCheckpointCheck._journalOverride</c>, so these run on every platform and never
///     touch a real volume.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexMidSessionCheckpointLossTests
{
    const ulong WatchedJournalId = 7;
    const long ArmedUsn = 1_000_000;
    const long AllocationDelta = 64;
    const long MaximumSize = 128L * 1024 * 1024;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static IDisposable Journal(ulong journalId, long firstUsn, long nextUsn)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(
            _ => new JournalWindow(journalId, firstUsn, nextUsn, AllocationDelta, MaximumSize));
    }

    static DriveStatus DriveFor(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(driveLetter));
    }

    /// <summary>The condition NTFS reports as ERROR_JOURNAL_ENTRY_DELETED, as the source ends a drive on.</summary>
    static DriveWatchFailure JournalEntriesDeleted(char driveLetter)
    {
        return new DriveWatchFailure(driveLetter,
            new IOException("USN journal entries have been deleted; full rescan needed"));
    }

    async Task<WatchHarness> StartedHarnessAsync(params IndexWatchTarget[] targets)
    {
        var harness = new WatchHarness(targets.Length > 0
            ? targets
            : [new IndexWatchTarget('T', WatchedJournalId, ArmedUsn)]);
        try
        {
            await harness.Index.StartWatchingAsync(Token);
            await harness.SourceStartedAsync();
            return harness;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    [TestMethod]
    public async Task WatchFaultOverATrimmedCheckpoint_ReportsTheSameLossAWarmStartWould()
    {
        using var harness = await StartedHarnessAsync();
        // The journal has moved on past the watch position by 500 bytes, and its tip is
        // 4000 bytes past it: the same window the warm-start path computes 4096 from.
        using var journal = Journal(WatchedJournalId,
            firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000);

        await harness.PublishAsync(JournalEntriesDeleted('T'));

        var drive = DriveFor(harness, 'T');
        Assert.IsNotNull(drive.WatchFailureMessage, "the watch still reports that it died");
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.WatchCatchUp);

        var loss = drive.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.DriveLetter);
        Assert.AreEqual(ArmedUsn, loss.CheckpointUsn);
        Assert.AreEqual(ArmedUsn + 500, loss.FirstUsn);
        Assert.AreEqual(ArmedUsn + 4_000, loss.NextUsn);
        Assert.AreEqual(AllocationDelta, loss.AllocationDelta);
        Assert.AreEqual(MaximumSize, loss.MaximumSize);
        Assert.AreEqual(500L, loss.BytesBehind);
        // The 4000-byte span rounds to 4032, then the trimming margin adds one 64-byte delta.
        Assert.AreEqual(4_096L, loss.SizeThatWouldHaveRetained);

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The position the loss reports is where the watch had actually reached, not where it
    ///     was armed: every applied batch advances the block's cursor, and that cursor is what
    ///     the journal is asked about.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultAfterAppliedBatches_ReportsThePositionTheWatchReached()
    {
        using var harness = await StartedHarnessAsync();
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "applied.txt")],
            JournalId: WatchedJournalId, NextUsn: ArmedUsn + 2_000));
        Assert.AreEqual(ArmedUsn + 2_000, harness.BlockFor('T').Header.UsnNextUsn);

        using var journal = Journal(WatchedJournalId,
            firstUsn: ArmedUsn + 2_500, nextUsn: ArmedUsn + 3_000);

        await harness.PublishAsync(JournalEntriesDeleted('T'));

        var loss = DriveFor(harness, 'T').CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(ArmedUsn + 2_000, loss.CheckpointUsn,
            "the watch position, not the cursor the drive was armed from");
        Assert.AreEqual(500L, loss.BytesBehind);

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task WatchFaultOverARecreatedJournal_ReportsTheLossWithNoSize()
    {
        using var harness = await StartedHarnessAsync();
        using var journal = Journal(journalId: 0xFEED, firstUsn: 0, nextUsn: 200);

        await harness.PublishAsync(JournalEntriesDeleted('T'));

        var loss = DriveFor(harness, 'T').CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.AreEqual('T', loss.DriveLetter);
        Assert.AreEqual(ArmedUsn, loss.CheckpointUsn);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The negative the whole design rests on: the classification is the journal's answer,
    ///     not the exception's, so a fault that has nothing to do with the journal leaves the
    ///     report null even though the journal was queried.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultWhileTheWatchPositionIsStillInTheJournal_LeavesTheLossNull()
    {
        using var harness = await StartedHarnessAsync();
        var queriedDrives = new List<char>();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
        {
            queriedDrives.Add(char.ToUpperInvariant(driveLetter));
            return new JournalWindow(WatchedJournalId, ArmedUsn - 500, ArmedUsn + 4_000,
                AllocationDelta, MaximumSize);
        });

        await harness.PublishAsync(new DriveWatchFailure('T',
            new UnauthorizedAccessException("the volume handle was revoked")));

        var drive = DriveFor(harness, 'T');
        Assert.IsNotNull(drive.WatchFailureMessage);
        Assert.IsNull(drive.CheckpointLoss, "a fault unrelated to the journal reports no checkpoint loss");
        CollectionAssert.AreEqual(new[] { 'T' }, queriedDrives,
            "the journal is asked once, and its answer is what decides");

        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task WatchFaultOnAVolumeThatCannotAnswer_LeavesTheLossNull()
    {
        using var harness = await StartedHarnessAsync();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);

        await harness.PublishAsync(JournalEntriesDeleted('T'));

        var drive = DriveFor(harness, 'T');
        Assert.IsNotNull(drive.WatchFailureMessage);
        Assert.IsNull(drive.CheckpointLoss, "a volume that cannot answer reports nothing rather than guessing");

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    /// <summary>One drive losing its position says nothing about another drive's.</summary>
    [TestMethod]
    public async Task OneDriveLosesItsPosition_AndTheOtherDriveKeepsNoReport()
    {
        using var harness = await StartedHarnessAsync(
            new IndexWatchTarget('T', WatchedJournalId, ArmedUsn),
            new IndexWatchTarget('U', WatchedJournalId, ArmedUsn));
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
            char.ToUpperInvariant(driveLetter) == 'T'
                ? new JournalWindow(WatchedJournalId, ArmedUsn + 500, ArmedUsn + 4_000,
                    AllocationDelta, MaximumSize)
                : new JournalWindow(WatchedJournalId, 0, ArmedUsn + 4_000, AllocationDelta, MaximumSize));

        await harness.PublishAsync(JournalEntriesDeleted('T'));
        await harness.PublishAsync(JournalEntriesDeleted('U'));

        Assert.IsNotNull(DriveFor(harness, 'T').CheckpointLoss);
        Assert.IsNull(DriveFor(harness, 'U').CheckpointLoss,
            "U's watch died too, but its position is still in the journal");

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     A fault announced for a drive letter this index has no block for has no position to
    ///     ask about, so nothing is queried and nothing is reported.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultOnADriveWithNoBlock_QueriesNoJournal()
    {
        using var harness = await StartedHarnessAsync();
        var queriedDrives = new List<char>();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
        {
            queriedDrives.Add(char.ToUpperInvariant(driveLetter));
            return null;
        });

        await harness.PublishAsync(new DriveWatchFailure('Q', new IOException("no such drive here")));

        Assert.AreEqual(0, queriedDrives.Count);
        Assert.IsTrue(harness.Index.Drives.All(drive => drive.CheckpointLoss is null));

        await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The mid-session report explains the block in place exactly as the warm-start one
    ///     does, so the rescan that replaces that block drops it.
    /// </summary>
    [TestMethod]
    public async Task ASuccessfulRescanClearsAMidSessionLoss()
    {
        // A second drive keeps the stream live after T faults, so the rescan takes the ordinary
        // disarm-swap-rearm path rather than restarting the whole session.
        using var harness = await StartedHarnessAsync(
            new IndexWatchTarget('T', WatchedJournalId, ArmedUsn),
            new IndexWatchTarget('U', WatchedJournalId, ArmedUsn));
        using (Journal(WatchedJournalId, firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000))
        {
            await harness.PublishAsync(JournalEntriesDeleted('T'));
            Assert.IsNotNull(DriveFor(harness, 'T').CheckpointLoss);
        }

        harness.SetNextProducedCursor('T', WatchedJournalId, ArmedUsn + 4_000);
        await harness.Index.RescanAsync('T', Token);

        Assert.IsNull(DriveFor(harness, 'T').CheckpointLoss,
            "the rescan replaced the block whose cursor the report described");
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
    }

    /// <summary>
    ///     A stream that ends while drives are still watched faults every one of them, and each
    ///     is classified against the journal the same way a per-drive failure is.
    /// </summary>
    [TestMethod]
    public async Task SourceEndingWithoutAStop_ClassifiesEveryWatchedDrive()
    {
        using var harness = await StartedHarnessAsync(
            new IndexWatchTarget('T', WatchedJournalId, ArmedUsn),
            new IndexWatchTarget('U', WatchedJournalId, ArmedUsn));
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
            char.ToUpperInvariant(driveLetter) == 'T'
                ? new JournalWindow(WatchedJournalId, ArmedUsn + 500, ArmedUsn + 4_000,
                    AllocationDelta, MaximumSize)
                : new JournalWindow(WatchedJournalId, 0, ArmedUsn + 4_000, AllocationDelta, MaximumSize));
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.WatchFaulted += _ => announced.TrySetResult();

        await harness.CompleteSourceAsync();
        // Waiting for the announcement, not for time: the source iterator's own end runs before
        // the pump observes it, and the report is recorded before the fault is announced.
        await announced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);

        var lost = DriveFor(harness, 'T').CheckpointLoss;
        Assert.IsNotNull(lost);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, lost.Cause);
        Assert.AreEqual(500L, lost.BytesBehind);
        Assert.IsNull(DriveFor(harness, 'U').CheckpointLoss);
    }
}
