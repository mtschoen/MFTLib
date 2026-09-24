using System.Collections;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A drive that was healthy when its rescan began but faulted while the rescan's producer was
///     still running. The rescan's entry-time check saw no fault, so these pin that a failed
///     production still leaves the newer fault, and the old cursor it condemns, alone.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexWatchRescanFaultDuringProductionTests
{
    static readonly IndexWatchTarget[] TwoDrives =
        [new('T', 11, 4242), new('U', 22, 8484)];

    public TestContext TestContext { get; set; } = null!;
    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static DriveStatus DriveFor(WatchHarness harness, char letter) =>
        harness.Index.Drives.Single(drive => drive.DriveLetter == letter);

    static IDisposable LostCheckpointForT() =>
        JournalCheckpointCheck.OverrideJournalForTest(letter => letter == 'T'
            ? new JournalWindow(11, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);

    /// <summary>
    ///     "running": T's already-delivered batch fails to apply mid-production while U keeps the
    ///     session alive. "ended": the same with T as the only drive, so the fault ends the session.
    ///     "stream-ended": the source completes mid-production, which faults every target,
    ///     including the disarmed T.
    /// </summary>
    [DataTestMethod]
    [DataRow("running")]
    [DataRow("ended")]
    [DataRow("stream-ended")]
    public async Task FaultRecordedDuringFailedProduction_IsKeptAndOldCursorIsNotRearmed(string shape)
    {
        using var harness = new WatchHarness(shape == "ended" ? [TwoDrives[0]] : TwoDrives);
        using var journal = LostCheckpointForT();
        var faultAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.WatchFaulted += fault =>
        {
            if (shape == "stream-ended" ? fault.Kind == WatchFaultKind.Source : fault.DriveLetter == 'T')
            {
                faultAnnounced.TrySetResult();
            }
        };
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        // The batch passes the source's arm-generation check before the rescan disarms T, which
        // is what a real stream does with an item it has already handed to the pump. Its apply
        // is parked until the rescan's producer is running, then fails.
        var applyFailure = new IOException("T's in-flight batch could not be applied");
        var applyGate = new TestGate();
        Task? consumed = null;
        if (shape != "stream-ended")
        {
            consumed = harness.Queue(new JournalBatch('T', new GatedFailingEntries(applyGate, applyFailure),
                JournalId: 11, NextUsn: 4300));
            await applyGate.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        }

        var original = harness.Index.Root('T').DriveBlock;
        harness.FailNextProduction('T', new IOException("rescan producer failed"));
        var production = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        var operationOffset = harness.WatchOperations.Count;
        CollectionAssert.Contains(harness.WatchOperations.ToArray(), "disarm:T");

        if (consumed is not null)
        {
            applyGate.Release();
            await consumed.WaitAsync(FakeIndexWatchSource.HangGuard);
        }
        else
        {
            await harness.CompleteSourceAsync();
        }

        await faultAnnounced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
        var faulted = DriveFor(harness, 'T');
        Assert.IsNotNull(faulted.WatchFailureMessage);
        Assert.IsNotNull(faulted.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, faulted.CheckpointLoss.DetectedDuring);

        production.Release();
        await rescan.WaitAsync(FakeIndexWatchSource.HangGuard);

        CollectionAssert.DoesNotContain(harness.WatchOperations.Skip(operationOffset).ToArray(), "arm:T");
        Assert.AreEqual(1, harness.SourceInvocationCount);
        var after = DriveFor(harness, 'T');
        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreEqual("rescan producer failed", after.MftProducerFailureMessage);
        Assert.AreEqual(faulted.WatchFailureMessage, after.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, after.WatchCatchUp);
        Assert.AreEqual(faulted.CheckpointLoss, after.CheckpointLoss);

        if (shape != "stream-ended")
        {
            var stopFailure = await Assert.ThrowsExceptionAsync<IOException>(
                () => harness.Index.StopWatchingAsync(Token));
            Assert.AreSame(applyFailure, stopFailure);
        }
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
