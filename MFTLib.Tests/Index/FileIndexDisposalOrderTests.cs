using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Disposal in the order spec section 5 gives it: cancel the disposal token, which every rescan
///     is linked to; retire every watch and await its drain; take every lifecycle gate, then every
///     write gate, in ascending drive-letter order; release the snapshots; and leave no gate held.
/// </summary>
[TestClass]
public class FileIndexDisposalOrderTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task Dispose_DuringGatedRescans_CancelsThemAwaitsPumpsReleasesGates()
    {
        using var harness = new WatchHarness('T', 'U', 'V');
        await harness.Index.StartWatchingAsync('V', Token);
        var watchOfV = harness.Source.WatchFor('V');
        var heldT = harness.HoldNextProduction('T');
        var heldU = harness.HoldNextProduction('U');
        var rescanT = harness.Index.RescanAsync('T', Token);
        var rescanU = harness.Index.RescanAsync('U', Token);
        await Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard);

        await harness.Index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await ThrowsAsync<OperationCanceledException>(() => rescanT.WaitAsync(HangGuard));
        await ThrowsAsync<OperationCanceledException>(() => rescanU.WaitAsync(HangGuard));
        Assert.AreEqual(1, watchOfV.DisposeCount, "disposal awaited V's pump, which disposed its handle");
        foreach (var driveLetter in "TUV")
        {
            Assert.IsTrue(harness.Index.AreDriveGatesFreeForTest(driveLetter),
                $"a gate of {driveLetter} is still held");
        }
    }

    [TestMethod]
    public async Task Rescan_TokenLinkedToDisposal()
    {
        using var harness = new WatchHarness('T');
        var held = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', CancellationToken.None);
        await held.Entered.WaitAsync(HangGuard);

        var disposal = harness.Index.DisposeAsync().AsTask();

        await ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard));
        await disposal.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task Dispose_TakesLifecycleGatesThenWaitsForABatchHoldingItsWriteGate()
    {
        using var harness = new WatchHarness('T', 'U');
        var batch = harness.TrackGate();
        harness.Index.ApplyJournalEntriesInsideWriteGateForTest = driveLetter =>
        {
            if (driveLetter == 'T')
            {
                batch.MarkEntered();
                batch.WaitForRelease();
            }
        };
        var index = harness.Index;
        var apply = Task.Run(() => index.ApplyJournalEntries('T', [WatchHarness.Create(9, "last.txt")],
            WatchHarness.JournalIdentifier, 600));
        await batch.Entered.WaitAsync(HangGuard);
        var lifecycleGatesTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.LifecycleGatesTakenForDisposalForTest = () => lifecycleGatesTaken.TrySetResult();

        var disposal = index.DisposeAsync().AsTask();
        await lifecycleGatesTaken.Task.WaitAsync(HangGuard);

        Assert.IsFalse(index.AreDriveGatesFreeForTest('U'), "disposal holds every lifecycle gate");
        Assert.IsFalse(disposal.IsCompleted, "disposal waits for the batch that holds T's write gate");
        batch.Release();
        var changes = await apply.WaitAsync(HangGuard);
        Assert.AreEqual(1, changes.Count, "the batch finished against a block that was still mapped");
        await disposal.WaitAsync(HangGuard);
    }
}
