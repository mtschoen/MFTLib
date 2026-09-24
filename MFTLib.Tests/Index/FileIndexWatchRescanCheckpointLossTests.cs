using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan-triggered restart after every drive faulted, where one drive's live watch found
///     its checkpoint gone from the journal. That drive needs its own rescan, so the restart
///     leaves it out rather than arming it from a cursor the journal already said it no longer
///     holds. The journal read is swapped out through
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
        // U's cursor (8484) has been trimmed out of its journal; T's volume cannot say.
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(driveLetter =>
            driveLetter == 'U'
                ? new JournalWindow(22, FirstUsn: 8484 + 500, NextUsn: 8484 + 4_000, AllocationDelta, MaximumSize)
                : null);
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        await harness.PublishAsync(new DriveWatchFailure('U',
            new IOException("USN journal entries have been deleted; full rescan needed")));
        await harness.SourceEndedAsync();

        var lostBefore = FileIndexWatchRescanEndedSessionTests.DriveFor(harness, 'U').CheckpointLoss;
        Assert.IsNotNull(lostBefore);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, lostBefore.DetectedDuring);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        var targets = await harness.SourceStartedAsync();
        Assert.AreEqual(2, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { new IndexWatchTarget('T', 13, 9000) }, targets.ToArray());

        var lostDrive = FileIndexWatchRescanEndedSessionTests.DriveFor(harness, 'U');
        Assert.AreEqual("USN journal entries have been deleted; full rescan needed", lostDrive.WatchFailureMessage);
        Assert.AreEqual(lostBefore, lostDrive.CheckpointLoss);
        Assert.AreEqual(WatchCatchUpState.Faulted, lostDrive.WatchCatchUp);
        Assert.IsNull(FileIndexWatchRescanEndedSessionTests.DriveFor(harness, 'T').WatchFailureMessage);

        // U's own fault is carried into the restarted session, so the stop still reports it.
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("USN journal entries have been deleted; full rescan needed", thrown.Message);
    }
}
