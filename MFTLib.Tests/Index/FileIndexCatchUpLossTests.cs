using System.Text.RegularExpressions;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A scan whose journal catch-up the producer reports lost (spec 2.6.6): its block is
///     published, unresumable, the drive's count rises, and the scan operation rescans the drive at
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
        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => pendingCatchUp);

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
        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(StandardCatchUpLoss('T'), drive.Watch.CheckpointLoss, "the retry keeps the report the loss produced");
        Assert.AreEqual(WatchState.CatchingUp, drive.Watch.State);
        Assert.AreSame(harness.BlockFor('T'), index.Root(TestContext.CancellationToken).DriveBlock.Block, "the second block is published");
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

        var failure = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var losses = CatchUpLosses(harness, 'T');
        var statusesAtLoss = harness.CatchUpLossStatuses('T');
        Assert.AreSequenceEqual([1, 2, 3], [.. statusesAtLoss.Select(status => status.Watch.ConsecutiveLostCatchUps)]);
        Assert.AreSequenceEqual([false, false, true], [.. losses.Select(loss => loss.IsRecoveryStopped)]);
        Assert.AreSequenceEqual([false, false, true], [.. statusesAtLoss.Select(status => status.Watch.IsRecoveryStopped)]);
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(StandardCatchUpLoss('T'), statusesAtLoss[^1].Watch.CheckpointLoss);

        Assert.AreSame(harness.BlockFor('T'), harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock.Block,
            "the drive keeps its third block, queryable");
        var drive = harness.DriveFor('T');
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(WatchState.Faulted, drive.Watch.State);
        Assert.AreEqual(failure.Message, drive.Watch.FailureMessage);
        Assert.Contains("T", drive.Watch.FailureMessage!);
        Assert.Contains("3", drive.Watch.FailureMessage!);
        Assert.Contains("12288", drive.Watch.FailureMessage!);
        Assert.Contains("BrokerSession.GrowUsnJournalAsync", failure.Message);
        Assert.IsFalse(failure.Message.Contains("BrokerProcess.GrowUsnJournalAsync", StringComparison.Ordinal));
        Assert.AreEqual(StandardCatchUpLoss('T'), drive.Watch.CheckpointLoss);
        Assert.AreEqual(3, drive.Watch.ConsecutiveLostCatchUps);

        var refusal = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StartWatchingAsync(Token));
        Assert.Contains("RescanAsync", refusal.Message);
    }

    [TestMethod]
    public async Task Rescan_SuccessResetsTheCount()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Held);
        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "a later single loss retries");
        var latest = CatchUpLosses(harness, 'T')[^1];
        Assert.AreEqual(1, harness.CatchUpLossStatuses('T')[^1].Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(latest.IsRecoveryStopped);
        Assert.AreEqual(0, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
    }

    [TestMethod]
    public async Task Rescan_ManualRescanAtTheLimit_MakesOneAttempt()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        var failure = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(4, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(harness.DriveFor('T').Watch.IsRecoveryStopped);

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.IsFalse(drive.Watch.IsRecoveryStopped);
        Assert.AreEqual(WatchState.NotStarted, drive.Watch.State);
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
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(2, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps,
            "a scan that produced no block leaves the count unchanged");

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        var failure = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(failure.IsRecoveryStopped);
    }

    [TestMethod]
    public async Task CatchUpLost_JournalRecreated_ReportHasNoSuggestion()
    {
        using var harness = new WatchHarness('T');
        var recreated = StandardCatchUpLoss('T') with
        {
            Cause = JournalCheckpointLossCause.JournalRecreated,
            BytesBehind = null,
            SizeThatWouldHaveRetained = null
        };
        harness.ScriptScans('T', new WatchHarnessScan(recreated), new WatchHarnessScan(recreated), new WatchHarnessScan(recreated));

        var failure = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(recreated, harness.CatchUpLossStatuses('T')[^1].Watch.CheckpointLoss);
        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(recreated, harness.DriveFor('T').Watch.CheckpointLoss);
        foreach (var message in CatchUpLosses(harness, 'T').Select(loss => loss.Message))
        {
            Assert.DoesNotMatchRegex(MyRegex(), message);
        }
    }

    [TestMethod]
    public async Task CatchUpFailureNotProven_ScanFailsWithNoBlock_CountUnchangedNoReport()
    {
        using var harness = new WatchHarness('T');
        var original = harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock;
        var producerFailure = new InvalidOperationException("the broker reported an error after ScanReady");
        harness.FailNextProduction('T', producerFailure);

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));

        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.Contains(producerFailure.Message, failure.Message);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(producerFailure.Message, drive.Failure?.Message);
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.Watch.CheckpointLoss);
        Assert.IsEmpty(CatchUpLosses(harness, 'T'));
        Assert.AreSame(original, harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock);
    }

    [TestMethod]
    public async Task CatchUpLost_ProducerFailure_IsNotCounted()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(() => harness.IndexFor('T').RescanAsync(Token));

        harness.FailNextProduction('T', new OperationCanceledException("the scan was cancelled"));
        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);

        harness.FailNextProduction('T', new InvalidOperationException("the producer failed"));
        Assert.IsTrue(harness.DriveFor('T').Watch.IsRecoveryStopped);
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.HasCount(3, CatchUpLosses(harness, 'T'), "no CatchUpLost fault for a producer failure");
        Assert.IsTrue(harness.DriveFor('T').Watch.IsRecoveryStopped);
        harness.ScriptScans('T', new WatchHarnessScan(Failure: new IOException("no block")));
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').RescanAsync(Token));
        Assert.IsTrue(harness.DriveFor('T').Watch.IsRecoveryStopped);
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').StartWatchingAsync(Token));
        await harness.IndexFor('T').StopWatchingAsync(Token);
        Assert.IsTrue(harness.DriveFor('T').Watch.IsRecoveryStopped, "stop must not erase exhaustion");
    }

    [TestMethod]
    public async Task CatchUpLost_CountsArePerDrive()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), Lost('T', hold: heldRetry), Lost('T'));
        var rescanOfT = harness.IndexFor('T').RescanAsync(Token);
        await heldRetry.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(9, "sibling.txt", nextUsn: 900));
        Assert.AreEqual(900L, harness.IndexFor('U').Root(TestContext.CancellationToken).DriveBlock.Block.Header.UsnNextUsn);
        harness.ScriptScans('U', Lost('U'), Held);
        harness.SetNextProducedCursor('U', WatchHarness.JournalIdentifier, 900);
        await harness.IndexFor('U').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(rescanOfT.IsCompleted, "T is still retrying");

        heldRetry.Release();
        await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(() => rescanOfT.WaitAsync(HangGuard, TestContext.CancellationToken));
        var driveT = harness.DriveFor('T');
        var driveU = harness.DriveFor('U');
        Assert.AreEqual(3, driveT.Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(driveT.Watch.IsRecoveryStopped);
        Assert.AreEqual(WatchState.Faulted, driveT.Watch.State);
        Assert.AreEqual(DriveState.Ready, driveU.State);
        Assert.AreEqual(0, driveU.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(driveU.Watch.IsRecoveryStopped);
        Assert.IsNull(driveU.Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, driveU.Watch.State);
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
        harness.Source.WatchFor('T').FailDrive(new IOException("T lost its checkpoint"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, harness.DriveFor('T').Watch.CheckpointLoss?.DetectedDuring);

        harness.ScriptScans('T', Lost('T'), Held);
        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(StandardCatchUpLoss('T'), harness.DriveFor('T').Watch.CheckpointLoss);
    }

    /// <summary>
    ///     A healthy watched drive whose first attempt publishes a lost catch-up no longer has the
    ///     old cursor to go back to, so when its retry produces no block the rescan reports a failed
    ///     result carrying the producer's failure and refuses the watch rather than restarting from the lost block.
    /// </summary>
    [TestMethod]
    public async Task Rescan_WatchedDrive_LostThenNoBlock_ReportsProducerFailureAndRefusesTheWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producerFailure = new IOException("the volume went away");
        harness.ScriptScans('T', Lost('T'), new WatchHarnessScan(Failure: producerFailure));

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.HasCount(1, harness.Source.TargetsFor('T'), "the lost block's cursor is never started");
        var drive = harness.DriveFor('T');
        Assert.AreEqual(1, drive.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.Faulted, drive.Watch.State);
        Assert.IsFalse(drive.Watch.IsRecoveryStopped, "an unresumable refusal is not exhaustion");
        Assert.Contains("RescanAsync", drive.Watch.FailureMessage!);
        Assert.AreEqual(StandardCatchUpLoss('T'), drive.Watch.CheckpointLoss);
    }

    [GeneratedRegex("bytes|[Gg]row")]
    private static partial Regex MyRegex();
}
