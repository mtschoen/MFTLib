using System.Collections;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>A current watch's apply fault during production remains a real fault of the drive.</summary>
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

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplyFailureDuringProduction_RecoversOnlyAfterAFailedScan(bool scanFails)
    {
        using var harness = new WatchHarness('T', 'U');
        using var journal = LostCheckpointForT();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var production = harness.HoldNextProduction('T');
        if (scanFails)
        {
            harness.FailNextProduction('T', new IOException("rescan producer failed"));
        }

        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        var handle = harness.Source.HandleFor('T');
        Assert.AreEqual(0, handle.DisposeCount);
        var applyFailure = new IOException("T's in-flight batch could not be applied");
        var applyGate = harness.TrackGate();
        var consumed = handle.Queue(new JournalBatch(new GatedFailingEntries(applyGate, applyFailure),
            WatchHarness.JournalId, WatchHarness.NextUsn + 300));
        await applyGate.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        applyGate.Release();
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        Assert.AreSame(applyFailure, fault.Exception);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, harness.DriveFor('T').CheckpointLoss!.DetectedDuring);
        production.Release();
        if (scanFails)
        {
            await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => rescan);
        }
        else
        {
            await rescan;
        }

        await consumed.WaitAsync(FakeIndexWatchSource.HangGuard);
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(scanFails ? 3 : 2, harness.ProductionCount('T'));
        Assert.AreEqual(1, harness.Faults.Count(item => item.Kind == WatchFaultKind.Apply));
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(9, "after.txt", nextUsn: 4500));
        Assert.AreEqual(4500L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Source.HandleFor('U').Publish(WatchHarness.Batch(9, "sibling.txt"));
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
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
