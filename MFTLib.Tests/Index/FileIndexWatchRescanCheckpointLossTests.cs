using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan of one drive after every drive faulted, where another drive's live watch found its
///     checkpoint gone from the journal. That drive needs its own rescan, so the rescan of the
///     first leaves it alone rather than starting it from a cursor the journal already said it no
///     longer holds. The journal read is swapped out through
///     <c>JournalCheckpointCheck._journalOverride</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexWatchRescanCheckpointLossTests
{
    const long AllocationDelta = 64;
    const long MaximumSize = 128L * 1024 * 1024;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task RescanAsync_AfterEveryDriveFaulted_LeavesADriveWhoseLiveWatchLostItsCheckpointOutOfTheRestart()
    {
        // U's cursor has been trimmed out of its journal; T's volume cannot say.
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
            driveLetter == 'U'
                ? new JournalWindow(WatchHarness.JournalId, FirstUsn: WatchHarness.NextUsn + 500,
                    NextUsn: WatchHarness.NextUsn + 4_000, AllocationDelta, MaximumSize)
                : null);
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        harness.Source.HandleFor('U').FailDrive(
            new IOException("USN journal entries have been deleted; full rescan needed"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        var uFault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'U');

        var lostBefore = harness.DriveFor('U').CheckpointLoss;
        Assert.IsNotNull(lostBefore);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, lostBefore.DetectedDuring);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.Source.StartsFor('T')[^1]);
        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        Assert.AreEqual(1, harness.Source.StartsFor('U').Count, "U's condemned cursor is never started again");

        var lostDrive = harness.DriveFor('U');
        Assert.AreEqual(uFault.Exception.Message, lostDrive.WatchFailureMessage);
        Assert.AreEqual(lostBefore, lostDrive.CheckpointLoss);
        Assert.AreEqual(WatchCatchUpState.Faulted, lostDrive.WatchCatchUp);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);

        // U's own fault is still outstanding, so its stop reports it.
        var thrown = await FileIndexWatchRescanTests.ThrowsAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('U', Token));
        Assert.AreSame(uFault.Exception, thrown);
    }
}
