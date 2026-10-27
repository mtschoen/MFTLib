using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Index;

namespace MFTLib.Tests.Index;

/// <summary>
///     Disposal in the order spec section 5 gives it: cancel the disposal token, which every rescan
///     is linked to; retire every watch and await its drain; take every lifecycle gate, then every
///     write gate, in ascending drive-letter order; release the snapshots; and leave no gate held.
/// </summary>
[TestClass]
public class FileIndexDisposalOrderTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task Dispose_DuringGatedRescans_CancelsThemAwaitsPumpsReleasesGates()
    {
        using var harness = new WatchHarness('T', 'U', 'V');
        await harness.Index.StartWatchingAsync([new VolumeKey('V')], Token);
        var watchOfV = harness.Source.WatchFor('V');
        var heldT = harness.HoldNextProduction('T');
        var heldU = harness.HoldNextProduction('U');
        var rescanT = harness.Index.RescanAsync([new VolumeKey('T')], Token);
        var rescanU = harness.Index.RescanAsync([new VolumeKey('U')], Token);
        await Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => rescanT.WaitAsync(HangGuard, TestContext.CancellationToken));
        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => rescanU.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(1, watchOfV.DisposeCount, "disposal awaited V's pump, which disposed its handle");
        foreach (var driveLetter in "TUV")
        {
            Assert.IsTrue(harness.Index.AreDriveGatesFreeForTest(new VolumeKey(driveLetter)),
                $"a gate of {driveLetter} is still held");
        }
    }

    [TestMethod]
    public async Task Rescan_TokenLinkedToDisposal()
    {
        using var harness = new WatchHarness('T');
        var held = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync([new VolumeKey('T')], CancellationToken.None);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var disposal = harness.Index.DisposeAsync().AsTask();

        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));
        await disposal.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Dispose_TakesLifecycleGatesThenWaitsForABatchHoldingItsWriteGate()
    {
        using var harness = new WatchHarness('T', 'U');
        var batch = harness.TrackGate();
        harness.Index.Hooks.ApplyJournalEntriesInsideWriteGate = driveLetter =>
        {
            if (driveLetter == new VolumeKey('T'))
            {
                batch.MarkEntered();
                batch.WaitForRelease();
            }
        };
        var index = harness.Index;
        var apply = Task.Run(() => index.ApplyJournalEntriesAsync('T', [WatchHarness.Create(9, "last.txt")],
            WatchHarness.JournalIdentifier, 600), TestContext.CancellationToken);
        await batch.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var lifecycleGatesTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Hooks.LifecycleGatesTakenForDisposal = () => lifecycleGatesTaken.TrySetResult();

        var disposal = index.DisposeAsync().AsTask();
        await lifecycleGatesTaken.Task.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsFalse(index.AreDriveGatesFreeForTest(new VolumeKey('U')), "disposal holds every lifecycle gate");
        Assert.IsFalse(disposal.IsCompleted, "disposal waits for the batch that holds T's write gate");
        batch.Release();
        var changes = await apply.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.HasCount(1, changes, "the batch finished against a block that was still mapped");
        await disposal.WaitAsync(HangGuard, TestContext.CancellationToken);
    }
}
