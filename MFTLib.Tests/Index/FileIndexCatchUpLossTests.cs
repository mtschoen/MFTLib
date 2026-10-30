using System.Text.RegularExpressions;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A scan whose journal catch-up the producer reports lost (spec 2.6.6): its block is
///     published, unresumable, the root's count rises, and the scan operation rescans the root at
///     once until the count reaches <see cref="FileIndex.LostCatchUpRecoveryLimit" />. The losses are
///     scripted on the producer's result, since the index trusts the producer's proof and reads no
///     journal for it; no clock is involved. One case records a live-watch loss through
///     <c>JournalIsolation.OverrideJournalWindow</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexCatchUpLossTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    /// <summary>A scan whose catch-up held.</summary>
    static WatchHarnessScan Held => new();

    [TestMethod]
    public async Task Rescan_CatchUpLostOnce_RetriesAtOnceAndKeepsTheReport()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var oldHandle = harness.Source.WatchFor('T');
        var production = harness.HoldNextProduction('T');
        var pendingCatchUp = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        harness.ScriptScans('T', Lost('T'), Held);
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9500);
        var index = harness.Index;
        WatchState? stateSeenByHandler = null;
        var retiredAtFirstLoss = false;
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.CatchUpLost)
            {
                retiredAtFirstLoss = oldHandle.DisposeCount == 1;
                stateSeenByHandler = index.Status.Watch.State;
            }
        };
        var producedBefore = harness.ProductionCount('T');

        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, oldHandle.DisposeCount, "a lost catch-up still keeps the watch during production");
        Assert.IsFalse(pendingCatchUp.IsCompleted);
        production.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await RootResultAssertions.FailureAsync<OperationCanceledException>(() => pendingCatchUp);

        Assert.IsTrue(retiredAtFirstLoss, "the first publication drained the old watch before reporting the loss");
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        var lost = CatchUpLosses(harness, 'T').Single();
        var atLoss = harness.CatchUpLossStatuses('T').Single();
        Assert.AreEqual(1, atLoss.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(atLoss.Watch.IsRecoveryStopped);
        Assert.IsFalse(lost.IsRecoveryStopped);
        Assert.AreEqual(StandardCatchUpLoss('T'), atLoss.Watch.CheckpointLoss);
        Assert.AreEqual(WatchState.Recovering, stateSeenByHandler,
            "Recovering is published before the fault is raised");
        var root = harness.RootFor('T');
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(StandardCatchUpLoss('T'), root.Watch.CheckpointLoss, "the retry keeps the report the loss produced");
        Assert.AreEqual(WatchState.CatchingUp, root.Watch.State);
        Assert.AreSame(harness.BlockFor('T'), index.Root(TestContext.CancellationToken).RootBlock.Block, "the second block is published");
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9500), harness.Source.TargetsFor('T')[^1],
            "the watch starts from the second block's cursor");
    }

    [TestMethod]
    public async Task Rescan_CatchUpLostThreeTimes_StopsAfterThreeProducerCalls()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');

        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var losses = CatchUpLosses(harness, 'T');
        var statusesAtLoss = harness.CatchUpLossStatuses('T');
        Assert.AreSequenceEqual([1, 2, 3], [.. statusesAtLoss.Select(status => status.Watch.ConsecutiveLostCatchUps)]);
        Assert.AreSequenceEqual([false, false, true], [.. losses.Select(loss => loss.IsRecoveryStopped)]);
        Assert.AreSequenceEqual([false, false, true], [.. statusesAtLoss.Select(status => status.Watch.IsRecoveryStopped)]);
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(StandardCatchUpLoss('T'), statusesAtLoss[^1].Watch.CheckpointLoss);

        Assert.AreSame(harness.BlockFor('T'), harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock.Block,
            "the root keeps its third block, queryable");
        var root = harness.RootFor('T');
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.AreEqual(failure.Message, root.Watch.FailureMessage);
        Assert.Contains("T", root.Watch.FailureMessage!);
        Assert.Contains("3", root.Watch.FailureMessage!);
        Assert.Contains("12288", root.Watch.FailureMessage!);
        Assert.Contains("BrokerSession.GrowUsnJournalAsync", failure.Message);
        Assert.IsFalse(failure.Message.Contains("BrokerProcess.GrowUsnJournalAsync", StringComparison.Ordinal));
        Assert.AreEqual(StandardCatchUpLoss('T'), root.Watch.CheckpointLoss);
        Assert.AreEqual(3, root.Watch.ConsecutiveLostCatchUps);

        var refusal = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StartWatchingAsync(Token));
        Assert.Contains("RescanAsync", refusal.Message);
    }

    [TestMethod]
    public async Task Rescan_SuccessResetsTheCount()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Held);
        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "a later single loss retries");
        var latest = CatchUpLosses(harness, 'T')[^1];
        Assert.AreEqual(1, harness.CatchUpLossStatuses('T')[^1].Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(latest.IsRecoveryStopped);
        Assert.AreEqual(0, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
    }

    [TestMethod]
    public async Task Rescan_ManualRescanAtTheLimit_MakesOneAttempt()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(4, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(harness.RootFor('T').Watch.IsRecoveryStopped);

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        var root = harness.RootFor('T');
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(root.Watch.FailureMessage);
        Assert.IsFalse(root.Watch.IsRecoveryStopped);
        Assert.AreEqual(WatchState.NotStarted, root.Watch.State);
        await harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the unresumable mark is cleared");
    }

    /// <summary>
    ///     The count spans operations: a consumer rescan that loses two catch-ups and then fails
    ///     without a block keeps the count, and the next rescan's loss is the third.
    /// </summary>
    [TestMethod]
    public async Task CatchUpLostCount_SurvivesOperations()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), new WatchHarnessScan(Failure: new IOException("the volume went away")));
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(2, harness.RootFor('T').Watch.ConsecutiveLostCatchUps,
            "a scan that produced no block leaves the count unchanged");

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(failure.IsRecoveryStopped);
    }

    [TestMethod]
    public async Task CatchUpLost_JournalRecreated_ReportHasNoSuggestion()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token);
        var recreated = StandardCatchUpLoss('T') with
        {
            Cause = JournalCheckpointLossCause.JournalRecreated,
            BytesBehind = null,
            SizeThatWouldHaveRetained = null
        };
        harness.ScriptScans('T', new WatchHarnessScan(recreated), new WatchHarnessScan(recreated), new WatchHarnessScan(recreated));

        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(recreated, harness.CatchUpLossStatuses('T')[^1].Watch.CheckpointLoss);
        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(recreated, harness.RootFor('T').Watch.CheckpointLoss);
        foreach (var message in CatchUpLosses(harness, 'T').Select(loss => loss.Message))
        {
            Assert.DoesNotMatchRegex(MyRegex(), message);
        }
    }

    [TestMethod]
    public async Task CatchUpFailureNotProven_ScanFailsWithNoBlock_CountUnchangedNoReport()
    {
        using var harness = new WatchHarness('T');
        var original = harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock;
        var producerFailure = new InvalidOperationException("the broker reported an error after ScanReady");
        harness.FailNextProduction('T', producerFailure);

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));

        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.Contains(producerFailure.Message, failure.Message);
        var root = harness.RootFor('T');
        Assert.AreEqual(producerFailure.Message, root.Failure?.Message);
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(root.Watch.CheckpointLoss);
        Assert.IsEmpty(CatchUpLosses(harness, 'T'));
        Assert.AreSame(original, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock);
    }

    [TestMethod]
    public async Task CatchUpLost_ProducerFailure_IsNotCounted()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(() => harness.IndexFor('T').RescanAsync(Token));

        harness.FailNextProduction('T', new OperationCanceledException("the scan was cancelled"));
        await RootResultAssertions.FailureAsync<OperationCanceledException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);

        harness.FailNextProduction('T', new InvalidOperationException("the producer failed"));
        Assert.IsTrue(harness.RootFor('T').Watch.IsRecoveryStopped);
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(3, harness.RootFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.HasCount(3, CatchUpLosses(harness, 'T'), "no CatchUpLost fault for a producer failure");
        Assert.IsTrue(harness.RootFor('T').Watch.IsRecoveryStopped);
        harness.ScriptScans('T', new WatchHarnessScan(Failure: new IOException("no block")));
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.IsTrue(harness.RootFor('T').Watch.IsRecoveryStopped);
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').StartWatchingAsync(Token));
        await harness.IndexFor('T').StopWatchingAsync(Token);
        Assert.IsTrue(harness.RootFor('T').Watch.IsRecoveryStopped, "stop must not erase exhaustion");
    }

    [TestMethod]
    public async Task CatchUpLost_CountsArePerRoot()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), Lost('T', hold: heldRetry), Lost('T'));
        var rescanOfT = harness.IndexFor('T').RescanAsync(Token);
        await heldRetry.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(9, "sibling.txt", nextUsn: 900));
        Assert.AreEqual(900L, harness.IndexFor('U').Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        harness.ScriptScans('U', Lost('U'), Held);
        harness.SetNextProducedCursor('U', WatchHarness.JournalIdentifier, 900);
        await harness.IndexFor('U').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(rescanOfT.IsCompleted, "T is still retrying");

        heldRetry.Release();
        await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(() => rescanOfT.WaitAsync(HangGuard, TestContext.CancellationToken));
        var rootT = harness.RootFor('T');
        var rootU = harness.RootFor('U');
        Assert.AreEqual(3, rootT.Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(rootT.Watch.IsRecoveryStopped);
        Assert.AreEqual(WatchState.Faulted, rootT.Watch.State);
        Assert.AreEqual(RootState.Ready, rootU.State);
        Assert.AreEqual(0, rootU.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(rootU.Watch.IsRecoveryStopped);
        Assert.IsNull(rootU.Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, rootU.Watch.State);
    }

    [TestMethod]
    public async Task CatchUpLost_ScanCatchUpReportReplacesLiveWatchReport()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(letter => letter == new VolumeKey('T')
            ? new SyntheticJournalWindow(WatchHarness.JournalIdentifier, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);
        using var harness = new WatchHarness('T');
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailVolume(new IOException("T lost its checkpoint"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, harness.RootFor('T').Watch.CheckpointLoss?.DetectedDuring);

        harness.ScriptScans('T', Lost('T'), Held);
        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(StandardCatchUpLoss('T'), harness.RootFor('T').Watch.CheckpointLoss);
    }

    /// <summary>
    ///     A healthy watched root whose first attempt publishes a lost catch-up no longer has the
    ///     old cursor to go back to, so when its retry produces no block the rescan reports a failed
    ///     result carrying the producer's failure and refuses the watch rather than restarting from the lost block.
    /// </summary>
    [TestMethod]
    public async Task Rescan_WatchedRoot_LostThenNoBlock_ReportsProducerFailureAndRefusesTheWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producerFailure = new IOException("the volume went away");
        harness.ScriptScans('T', Lost('T'), new WatchHarnessScan(Failure: producerFailure));

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the lost block's cursor is never started");
        var root = harness.RootFor('T');
        Assert.AreEqual(1, root.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.IsFalse(root.Watch.IsRecoveryStopped, "an unresumable refusal is not exhaustion");
        Assert.Contains("RescanAsync", root.Watch.FailureMessage!);
        Assert.AreEqual(StandardCatchUpLoss('T'), root.Watch.CheckpointLoss);
    }

    [GeneratedRegex("bytes|[Gg]row")]
    private static partial Regex MyRegex();
}
