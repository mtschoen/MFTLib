using System.Collections;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A drive that was healthy when its rescan began and whose retiring pump then fails while
///     applying a batch it had already accepted. The retired pump's fault belongs to no current
///     watch, so it is not recorded against the drive, and the failed production still restarts
///     the healthy watch from its old cursor.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexWatchRescanFaultDuringProductionTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static IDisposable LostCheckpointForT() =>
        JournalCheckpointCheck.OverrideJournalForTest(letter => letter == 'T'
            ? new JournalWindow(WatchHarness.JournalId, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);

    [TestMethod]
    public async Task ApplyFailureOfTheRetiringPump_DuringRescan_IsNotRecordedAndTheWatchRestartsFromTheOldCursor()
    {
        using var harness = new WatchHarness('T', 'U');
        using var journal = LostCheckpointForT();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);

        // The pump accepts a batch and parks inside its apply. The rescan retires the watch and
        // waits for that pump, so the apply fails only once the rescan is under way.
        var applyFailure = new IOException("T's in-flight batch could not be applied");
        var applyGate = new TestGate();
        var consumed = harness.Source.HandleFor('T').Queue(new JournalBatch(
            new GatedFailingEntries(applyGate, applyFailure), WatchHarness.JournalId, WatchHarness.NextUsn + 300));
        await applyGate.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);

        var original = harness.Index.Root('T').DriveBlock;
        harness.FailNextProduction('T', new IOException("rescan producer failed"));
        var rescan = harness.Index.RescanAsync('T', Token);
        Assert.IsFalse(rescan.IsCompleted, "the rescan waits for the retiring pump to drain");

        applyGate.Release();
        await FileIndexWatchRescanTests.ThrowsAsync<InvalidOperationException>(
            () => rescan.WaitAsync(FakeIndexWatchSource.HangGuard));
        await consumed.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(0, harness.Faults.Count(fault => fault.DriveLetter == 'T'),
            "the retired pump's failure belongs to no current watch");
        var after = harness.DriveFor('T');
        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual("rescan producer failed", after.MftProducerFailureMessage);
        Assert.IsNull(after.WatchFailureMessage);
        Assert.IsNull(after.CheckpointLoss, "the retired pump's fault ran no checkpoint check");
        Assert.AreEqual(WatchCatchUpState.CatchingUp, after.WatchCatchUp);
        var starts = harness.Source.StartsFor('T');
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(new IndexWatchTarget('T', WatchHarness.JournalId, WatchHarness.NextUsn), starts[1]);

        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(9, "after.txt", nextUsn: 4500));
        Assert.AreEqual(4500L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    /// <summary>
    ///     Journal entries whose enumeration parks on a gate and then throws, so the pump is held
    ///     inside the apply of an item it has already accepted, and the apply then fails.
    /// </summary>
    sealed class GatedFailingEntries(TestGate gate, Exception failure) : IReadOnlyList<UsnJournalEntry>
    {
        public int Count => 1;

        public UsnJournalEntry this[int index] => throw failure;

        public IEnumerator<UsnJournalEntry> GetEnumerator()
        {
            gate.MarkEntered();
            gate.WaitForRelease();
            throw failure;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
