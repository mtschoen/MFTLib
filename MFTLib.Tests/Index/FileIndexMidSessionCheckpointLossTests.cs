using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalWindows;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a consumer sees when a live watch dies because the journal has moved past the
///     position that watch was reading from. The root's block header cursor is the watch
///     position, so the same <see cref="JournalCheckpointCheck" /> that decides a warm start
///     answers here too, and the answer is a fact about the journal rather than about the
///     exception that ended the watch. The journal read is swapped out through
///     <c>JournalIsolation.OverrideJournalWindow</c>, so these run on every platform and
///     never touch a real volume. Each volume's pump asks about its own volume, so an override
///     callback can run on several pump threads at once and keeps no unsynchronized state.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexMidSessionCheckpointLossTests
{
    const ulong WatchedJournalIdentifier = WatchHarness.JournalIdentifier;
    const long ArmedUsn = WatchHarness.NextUsn;
    const long AllocationDelta = 64;
    const long MaximumSize = 128L * 1024 * 1024;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    /// <summary>T has lost its position by 500 bytes; every other volume's is still inside the journal.</summary>
    static IDisposable TrimmedForTOnly()
    {
        return Override(driveLetter =>
            char.ToUpperInvariant(driveLetter.Letter) == 'T'
                ? Window(WatchedJournalIdentifier, ArmedUsn + 500, ArmedUsn + 4_000)
                : Window(WatchedJournalIdentifier, 0, ArmedUsn + 4_000));
    }

    async Task<WatchHarness> StartedHarnessAsync(params char[] driveLetters)
    {
        var harness = new WatchHarness(driveLetters.Length > 0 ? driveLetters : ['T']);
        harness.HoldEveryRecovery();
        try
        {
            foreach (var index in harness.Indices)
            {
                await index.StartWatchingAsync(Token);
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
    ///     Ends a volume's watch with the condition NTFS reports as ERROR_JOURNAL_ENTRY_DELETED,
    ///     and returns once the fault has been announced, which is after the checkpoint-loss
    ///     check ran.
    /// </summary>
    static Task<WatchFault> FailWithJournalEntriesDeletedAsync(WatchHarness harness, char driveLetter)
    {
        return FailVolumeAsync(harness, driveLetter,
            new IOException("USN journal entries have been deleted; full rescan needed"));
    }

    static Task<WatchFault> FailVolumeAsync(WatchHarness harness, char driveLetter, Exception failure)
    {
        var announced = harness.WaitForFaultAsync(WatchFaultKind.Volume, driveLetter);
        harness.Source.WatchFor(driveLetter).FailVolume(failure);
        return announced;
    }

    [TestMethod]
    public async Task WatchFaultOverATrimmedCheckpoint_ReportsTheSameLossAWarmStartWould()
    {
        using var journal = Journal(WatchedJournalIdentifier,
            firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000);
        using var harness = await StartedHarnessAsync();
        // The journal has moved on past the watch position by 500 bytes, and its tip is
        // 4000 bytes past it: the same window the warm-start path computes 4096 from.

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var root = harness.RootFor('T');
        Assert.IsNotNull(root.Watch.FailureMessage, "the watch still reports that it died");
        Assert.AreEqual(WatchState.Recovering, root.Watch.State);

        var loss = root.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.Volume);
        Assert.AreEqual(ArmedUsn, loss.CheckpointUsn);
        Assert.AreEqual(ArmedUsn + 500, loss.FirstUsn);
        Assert.AreEqual(ArmedUsn + 4_000, loss.NextUsn);
        Assert.AreEqual(AllocationDelta, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(MaximumSize, loss.JournalSettings.MaximumSize);
        Assert.AreEqual(500L, loss.BytesBehind);
        // The 4000-byte span rounds to 4032, then the trimming margin adds one 64-byte delta.
        Assert.AreEqual(4_096L, loss.SizeThatWouldHaveRetained);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The position the loss reports is where the watch had actually reached, not where it
    ///     was armed: every applied batch advances the block's cursor, and that cursor is what
    ///     the journal is asked about.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultAfterAppliedBatches_ReportsThePositionTheWatchReached()
    {
        using var journal = Journal(WatchedJournalIdentifier,
            firstUsn: ArmedUsn + 2_500, nextUsn: ArmedUsn + 3_000);
        using var harness = await StartedHarnessAsync();
        await harness.Source.WatchFor('T').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "applied.txt")],
            JournalIdentifier: WatchedJournalIdentifier, NextUsn: ArmedUsn + 2_000));
        Assert.AreEqual(ArmedUsn + 2_000, harness.BlockFor('T').Header.UsnNextUsn);


        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var loss = harness.RootFor('T').Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(ArmedUsn + 2_000, loss.CheckpointUsn,
            "the watch position, not the cursor the root was armed from");
        Assert.AreEqual(500L, loss.BytesBehind);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task WatchFaultOverARecreatedJournal_ReportsTheLossWithNoSize()
    {
        using var journal = Journal(journalIdentifier: 0xFEED, firstUsn: 0, nextUsn: 200);
        using var harness = await StartedHarnessAsync();

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var loss = harness.RootFor('T').Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.AreEqual('T', loss.Volume);
        Assert.AreEqual(ArmedUsn, loss.CheckpointUsn);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The negative the whole design rests on: the classification is the journal's answer,
    ///     not the exception's, so a fault that has nothing to do with the journal leaves the
    ///     report null even though the journal was queried.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultWhileTheWatchPositionIsStillInTheJournal_LeavesTheLossNull()
    {
        var queriedDrives = new ConcurrentQueue<char>();
        using var journal = JournalIsolation.OverrideJournalWindow(driveLetter =>
        {
            queriedDrives.Enqueue(char.ToUpperInvariant(driveLetter.Letter));
            return new SyntheticJournalWindow(WatchedJournalIdentifier, ArmedUsn - 50, ArmedUsn + 4_000,
                AllocationDelta, MaximumSize);
        });
        using var harness = await StartedHarnessAsync();

        await FailVolumeAsync(harness, 'T', new UnauthorizedAccessException("the volume handle was revoked"));

        var root = harness.RootFor('T');
        Assert.IsNotNull(root.Watch.FailureMessage);
        Assert.IsNull(root.Watch.CheckpointLoss, "a fault unrelated to the journal reports no checkpoint loss");
        Assert.AreSequenceEqual(['T'], [.. queriedDrives], "the journal is asked once, and its answer is what decides");

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task WatchFaultOnAVolumeThatCannotAnswer_LeavesTheLossNull()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);
        using var harness = await StartedHarnessAsync();

        await FailWithJournalEntriesDeletedAsync(harness, 'T');

        var root = harness.RootFor('T');
        Assert.IsNotNull(root.Watch.FailureMessage);
        Assert.IsNull(root.Watch.CheckpointLoss, "a volume that cannot answer reports nothing rather than guessing");

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    /// <summary>One root losing its position says nothing about another root's.</summary>
    [TestMethod]
    public async Task OneRootLosesItsPosition_AndTheOtherRootKeepsNoReport()
    {
        using var journal = TrimmedForTOnly();
        using var harness = await StartedHarnessAsync('T', 'U');

        await FailWithJournalEntriesDeletedAsync(harness, 'T');
        await FailWithJournalEntriesDeletedAsync(harness, 'U');

        Assert.IsNotNull(harness.RootFor('T').Watch.CheckpointLoss);
        Assert.IsNull(harness.RootFor('U').Watch.CheckpointLoss,
            "U's watch died too, but its position is still in the journal");

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('U').StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The mid-session report explains the block in place exactly as the warm-start one
    ///     does, so the rescan that replaces that block drops it.
    /// </summary>
    [TestMethod]
    public async Task ASuccessfulRescanClearsAMidSessionLoss()
    {
        using var harness = await StartedHarnessAsync('T', 'U');
        using (Journal(WatchedJournalIdentifier, firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000))
        {
            await FailWithJournalEntriesDeletedAsync(harness, 'T');
            Assert.IsNotNull(harness.RootFor('T').Watch.CheckpointLoss);
        }

        harness.SetNextProducedCursor('T', WatchedJournalIdentifier, ArmedUsn + 4_000);
        await harness.IndexFor('T').RescanAsync(Token);

        Assert.IsNull(harness.RootFor('T').Watch.CheckpointLoss,
            "the rescan replaced the block whose cursor the report described");
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.IsNull(harness.RootFor('U').Watch.FailureMessage);
    }

    /// <summary>
    ///     A watch that ends without anyone stopping it is a fault of its own, and each root's
    ///     is classified against that volume's journal the same way a root failure is.
    /// </summary>
    [TestMethod]
    public async Task WatchEndingWithoutAStop_ClassifiesEachRootAgainstItsOwnJournal()
    {
        using var journal = TrimmedForTOnly();
        using var harness = await StartedHarnessAsync('T', 'U');
        var announcedT = harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        var announcedU = harness.WaitForFaultAsync(WatchFaultKind.Channel, 'U');

        harness.Source.WatchFor('T').End();
        harness.Source.WatchFor('U').End();
        // Waiting for the announcements, not for time: the report is recorded before the fault
        // is announced.
        await announcedT;
        await announcedU;

        var lost = harness.RootFor('T').Watch.CheckpointLoss;
        Assert.IsNotNull(lost);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, lost.Cause);
        Assert.AreEqual(500L, lost.BytesBehind);
        Assert.IsNull(harness.RootFor('U').Watch.CheckpointLoss);
    }

    /// <summary>
    ///     A <see cref="FileIndex.WatchFaulted" /> handler decides whether to blame the journal
    ///     by reading <see cref="RootWatchStatus.CheckpointLoss" />, so the live-watch report must
    ///     already be on the root when the handler runs, not recorded after it returns.
    /// </summary>
    [TestMethod]
    public async Task WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised()
    {
        using var journal = Journal(WatchedJournalIdentifier,
            firstUsn: ArmedUsn + 500, nextUsn: ArmedUsn + 4_000);
        using var harness = await StartedHarnessAsync();
        var seenByHandler = new TaskCompletionSource<JournalCheckpointLoss?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var index = harness.Index;
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Volume)
            {
                seenByHandler.TrySetResult(index.Status.Watch.CheckpointLoss);
            }
        };

        harness.Source.WatchFor('T').FailVolume(
            new IOException("USN journal entries have been deleted; full rescan needed"));

        var loss = await seenByHandler.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsNotNull(loss, "the handler must see the loss the fault found");
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }
}
