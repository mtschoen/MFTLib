using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan of one root after every root faulted, where another root's live watch found its
///     checkpoint gone from the journal. That volume needs its own rescan, so the rescan of the
///     first leaves it alone rather than starting it from a cursor the journal already said it no
///     longer holds. The journal read is swapped out through
///     <c>JournalIsolation.OverrideJournalWindow</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexWatchRescanCheckpointLossTests
{
    const long AllocationDelta = 64;
    const long MaximumSize = 128L * 1024 * 1024;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task RescanAsync_AfterEveryRootFaulted_LeavesARootWhoseLiveWatchLostItsCheckpointOutOfTheRestart()
    {
        // U's cursor has been trimmed out of its journal; T's volume cannot say.
        using var journal = JournalIsolation.OverrideJournalWindow(driveLetter =>
            driveLetter == 'U'
                ? new SyntheticJournalWindow(WatchHarness.JournalIdentifier, FirstUsn: WatchHarness.NextUsn + 500,
                    NextUsn: WatchHarness.NextUsn + 4_000, AllocationDelta, MaximumSize)
                : null);
        using var harness = new WatchHarness('T', 'U');
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        harness.Source.WatchFor('U').FailVolume(
            new IOException("USN journal entries have been deleted; full rescan needed"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        var uFault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'U');

        var lostBefore = harness.RootFor('U').Watch.CheckpointLoss;
        Assert.IsNotNull(lostBefore);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, lostBefore.DetectedDuring);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);

        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.Source.TargetsFor('T')[^1]);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.HasCount(1, harness.Source.TargetsFor('U'), "U's condemned cursor is never started again");

        var lostRoot = harness.RootFor('U');
        Assert.AreEqual(uFault.Exception.Message, lostRoot.Watch.FailureMessage);
        Assert.AreEqual(lostBefore, lostRoot.Watch.CheckpointLoss);
        Assert.AreEqual(WatchState.Recovering, lostRoot.Watch.State, "U's recovery is still held");
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);

        // U's own fault is still outstanding, so its stop reports it.
        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('U').StopWatchingAsync(Token));
        Assert.AreSame(uFault.Exception, failure);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }
}
