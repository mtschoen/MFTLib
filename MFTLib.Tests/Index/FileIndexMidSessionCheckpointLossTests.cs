using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a consumer sees when a live watch dies because the journal has moved past the
///     position that watch was reading from. The drive's block header cursor is the watch
///     position, so the same <see cref="JournalCheckpointCheck" /> that decides a warm start
///     answers here too, and the answer is a fact about the journal rather than about the
///     exception that ended the watch. The journal read is swapped out through
///     <c>JournalCheckpointCheck.OverrideJournalForTest</c>, so these run on every platform and
///     never touch a real volume. Each drive's pump asks about its own drive, so an override
///     callback can run on several pump threads at once and keeps no unsynchronized state.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexMidSessionCheckpointLossTests
{
    const ulong WatchedJournalId = WatchHarness.JournalId;
    const long ArmedUsn = WatchHarness.NextUsn;
    const long AllocationDelta = 64;
    const long MaximumSize = 128L * 1024 * 1024;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static IDisposable Journal(ulong journalId, long firstUsn, long nextUsn)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(
            _ => new JournalWindow(journalId, firstUsn, nextUsn, AllocationDelta, MaximumSize));
    }

    /// <summary>T has lost its position by 500 bytes; every other drive's is still inside the journal.</summary>
    static IDisposable TrimmedForTOnly()
    {
        return JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
            char.ToUpperInvariant(driveLetter) == 'T'
                ? new JournalWindow(WatchedJournalId, ArmedUsn + 500, ArmedUsn + 4_000,
                    AllocationDelta, MaximumSize)
                : new JournalWindow(WatchedJournalId, 0, ArmedUsn + 4_000, AllocationDelta, MaximumSize));
    }

    async Task<WatchHarness> StartedHarnessAsync(params char[] driveLetters)
    {
        var harness = new WatchHarness(driveLetters.Length > 0 ? driveLetters : ['T']);
        harness.Index.HoldEveryRecovery();
        try
        {
            foreach (var driveLetter in harness.Index.Drives.Select(drive => drive.DriveLetter))
            {
                await harness.Index.StartWatchingAsync(driveLetter, Token);
            }

            return harness;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Ends a drive's watch with the condition NTFS reports as ERROR_JOURNAL_ENTRY_DELETED,
    ///     and returns once the fault has been announced, which is after the checkpoint-loss
    ///     check ran.
    /// </summary>
    static Task<WatchFault> FailWithJournalEntriesDeletedAsync(WatchHarness harness, char driveLetter)
    {
        return FailDriveAsync(harness, driveLetter,
            new IOException("USN journal entries have been deleted; full rescan needed"));
    }

    static Task<WatchFault> FailDriveAsync(WatchHarness harness, char driveLetter, Exception failure)
    {
        var announced = harness.WaitForFaultAsync(WatchFaultKind.Drive, driveLetter);
        harness.Source.WatchFor(driveLetter).FailDrive(failure);
        return announced;
    }

    [TestMethod]
    public async Task WatchFaultOverATrimmedCheckpoint_ReportsTheSameLossAWarmStartWould()
    {
        using var harness = await StartedHarnessAsync();
        // The journal has moved on past the watch position by 500 bytes, and its tip is
        // 4000 bytes past it: the same window the warm-start path computes 4096 from.
        using var journal = Journal(WatchedJournalId,
            firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000);

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var drive = harness.DriveFor('T');
        Assert.IsNotNull(drive.WatchFailureMessage, "the watch still reports that it died");
        Assert.AreEqual(WatchCatchUpState.Recovering, drive.WatchCatchUp);

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

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
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
        await harness.Source.WatchFor('T').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "applied.txt")],
            JournalId: WatchedJournalId, NextUsn: ArmedUsn + 2_000));
        Assert.AreEqual(ArmedUsn + 2_000, harness.BlockFor('T').Header.UsnNextUsn);

        using var journal = Journal(WatchedJournalId,
            firstUsn: ArmedUsn + 2_500, nextUsn: ArmedUsn + 3_000);

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var loss = harness.DriveFor('T').CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(ArmedUsn + 2_000, loss.CheckpointUsn,
            "the watch position, not the cursor the drive was armed from");
        Assert.AreEqual(500L, loss.BytesBehind);

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task WatchFaultOverARecreatedJournal_ReportsTheLossWithNoSize()
    {
        using var harness = await StartedHarnessAsync();
        using var journal = Journal(journalId: 0xFEED, firstUsn: 0, nextUsn: 200);

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var loss = harness.DriveFor('T').CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.AreEqual('T', loss.DriveLetter);
        Assert.AreEqual(ArmedUsn, loss.CheckpointUsn);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
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
        var queriedDrives = new ConcurrentQueue<char>();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
        {
            queriedDrives.Enqueue(char.ToUpperInvariant(driveLetter));
            return new JournalWindow(WatchedJournalId, ArmedUsn - 50, ArmedUsn + 4_000,
                AllocationDelta, MaximumSize);
        });

        await FailDriveAsync(harness, 'T', new UnauthorizedAccessException("the volume handle was revoked"));

        var drive = harness.DriveFor('T');
        Assert.IsNotNull(drive.WatchFailureMessage);
        Assert.IsNull(drive.CheckpointLoss, "a fault unrelated to the journal reports no checkpoint loss");
        CollectionAssert.AreEqual(new[] { 'T' }, queriedDrives.ToArray(),
            "the journal is asked once, and its answer is what decides");

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task WatchFaultOnAVolumeThatCannotAnswer_LeavesTheLossNull()
    {
        using var harness = await StartedHarnessAsync();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var drive = harness.DriveFor('T');
        Assert.IsNotNull(drive.WatchFailureMessage);
        Assert.IsNull(drive.CheckpointLoss, "a volume that cannot answer reports nothing rather than guessing");

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
    }

    /// <summary>One drive losing its position says nothing about another drive's.</summary>
    [TestMethod]
    public async Task OneDriveLosesItsPosition_AndTheOtherDriveKeepsNoReport()
    {
        using var harness = await StartedHarnessAsync('T', 'U');
        using var journal = TrimmedForTOnly();

        await FailWithJournalEntriesDeletedAsync(harness, 'T');
        await FailWithJournalEntriesDeletedAsync(harness, 'U');

        Assert.IsNotNull(harness.DriveFor('T').CheckpointLoss);
        Assert.IsNull(harness.DriveFor('U').CheckpointLoss,
            "U's watch died too, but its position is still in the journal");

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('U', Token));
    }

    /// <summary>
    ///     The mid-session report explains the block in place exactly as the warm-start one
    ///     does, so the rescan that replaces that block drops it.
    /// </summary>
    [TestMethod]
    public async Task ASuccessfulRescanClearsAMidSessionLoss()
    {
        using var harness = await StartedHarnessAsync('T', 'U');
        using (Journal(WatchedJournalId, firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000))
        {
            await FailWithJournalEntriesDeletedAsync(harness, 'T');
            Assert.IsNotNull(harness.DriveFor('T').CheckpointLoss);
        }

        harness.SetNextProducedCursor('T', WatchedJournalId, ArmedUsn + 4_000);
        await harness.Index.RescanAsync('T', Token);

        Assert.IsNull(harness.DriveFor('T').CheckpointLoss,
            "the rescan replaced the block whose cursor the report described");
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.IsNull(harness.DriveFor('U').WatchFailureMessage);
    }

    /// <summary>
    ///     A watch that ends without anyone stopping it is a fault of its own, and each drive's
    ///     is classified against that drive's journal the same way a drive failure is.
    /// </summary>
    [TestMethod]
    public async Task WatchEndingWithoutAStop_ClassifiesEachDriveAgainstItsOwnJournal()
    {
        using var harness = await StartedHarnessAsync('T', 'U');
        using var journal = TrimmedForTOnly();
        var announcedT = harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        var announcedU = harness.WaitForFaultAsync(WatchFaultKind.Channel, 'U');

        harness.Source.WatchFor('T').End();
        harness.Source.WatchFor('U').End();
        // Waiting for the announcements, not for time: the report is recorded before the fault
        // is announced.
        await announcedT;
        await announcedU;

        var lost = harness.DriveFor('T').CheckpointLoss;
        Assert.IsNotNull(lost);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, lost.Cause);
        Assert.AreEqual(500L, lost.BytesBehind);
        Assert.IsNull(harness.DriveFor('U').CheckpointLoss);
    }

    /// <summary>
    ///     A <see cref="FileIndex.WatchFaulted" /> handler decides whether to blame the journal
    ///     by reading <see cref="DriveStatus.CheckpointLoss" />, so the live-watch report must
    ///     already be on the drive when the handler runs, not recorded after it returns.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised()
    {
        using var harness = await StartedHarnessAsync();
        using var journal = Journal(WatchedJournalId,
            firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000);
        var seenByHandler = new TaskCompletionSource<JournalCheckpointLoss?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var index = harness.Index;
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Drive)
            {
                seenByHandler.TrySetResult(index.Drives.Single(drive => drive.DriveLetter == 'T').CheckpointLoss);
            }
        };

        harness.Source.WatchFor('T').FailDrive(
            new IOException("USN journal entries have been deleted; full rescan needed"));

        var loss = await seenByHandler.Task.WaitAsync(ScriptedWatchSource.HangGuard);
        Assert.IsNotNull(loss, "the handler must see the loss the fault found");
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);

        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
    }
}
