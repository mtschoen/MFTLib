using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A scan whose journal catch-up the producer reports lost (spec 2.6.6): its block is
///     published, unresumable, the drive's count rises, and the scan operation rescans the drive at
///     once until the count reaches <see cref="FileIndex.LostCatchUpRecoveryLimit" />. The losses are
///     scripted on the producer's result, since the index trusts the producer's proof and reads no
///     journal for it; no clock is involved. One case records a live-watch loss through
///     <c>JournalCheckpointCheck.OverrideJournalForTest</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexCatchUpLossTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    /// <summary>A scan whose catch-up the producer reports lost with the standard loss.</summary>
    static WatchHarnessScan Lost(char driveLetter, TestGate? hold = null) =>
        new(StandardCatchUpLoss(driveLetter), Hold: hold);

    /// <summary>A scan whose catch-up held.</summary>
    static WatchHarnessScan Held => new();

    [TestMethod]
    public async Task Rescan_CatchUpLostOnce_RetriesAtOnceAndKeepsTheReport()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        var oldHandle = harness.Source.WatchFor('T');
        var production = harness.HoldNextProduction('T');
        var pendingCatchUp = harness.Index.WaitForCatchUpAsync('T', Token);
        harness.ScriptScans('T', Lost('T'), Held);
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9500);
        var index = harness.Index;
        WatchCatchUpState? stateSeenByHandler = null;
        var retiredAtFirstLoss = false;
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.CatchUpLost)
            {
                retiredAtFirstLoss = oldHandle.DisposeCount == 1;
                stateSeenByHandler = index.Drives.Single(drive => drive.DriveLetter == 'T').Watch.CatchUpState;
            }
        };
        var producedBefore = harness.ProductionCount('T');

        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(HangGuard);
        Assert.AreEqual(0, oldHandle.DisposeCount, "a lost catch-up still keeps the watch during production");
        Assert.IsFalse(pendingCatchUp.IsCompleted);
        production.Release();
        await rescan.WaitAsync(HangGuard);
        await ThrowsAsync<OperationCanceledException>(() => pendingCatchUp);

        Assert.IsTrue(retiredAtFirstLoss, "the first publication drained the old watch before reporting the loss");
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        var lost = WatchDeduplicationTestSupport.CatchUpLosses(harness, 'T').Single();
        var atLoss = harness.CatchUpLossStatuses('T').Single();
        Assert.AreEqual(1, atLoss.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(atLoss.Watch.RecoveryStopped);
        Assert.IsFalse(lost.RecoveryStopped);
        Assert.AreEqual(WatchDeduplicationTestSupport.StandardCatchUpLoss('T'), atLoss.Watch.CheckpointLoss);
        Assert.AreEqual(WatchCatchUpState.Recovering, stateSeenByHandler,
            "Recovering is published before the fault is raised");
        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchDeduplicationTestSupport.StandardCatchUpLoss('T'), drive.Watch.CheckpointLoss, "the retry keeps the report the loss produced");
        Assert.AreEqual(WatchCatchUpState.CatchingUp, drive.Watch.CatchUpState);
        Assert.AreSame(harness.BlockFor('T'), index.Root('T').DriveBlock.Block, "the second block is published");
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9500), harness.Source.TargetsFor('T')[^1],
            "the watch starts from the second block's cursor");
    }

    [TestMethod]
    public async Task Rescan_CatchUpLostThreeTimes_StopsAfterThreeProducerCalls()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');

        var thrown = await ThrowsAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var losses = WatchDeduplicationTestSupport.CatchUpLosses(harness, 'T');
        var statusesAtLoss = harness.CatchUpLossStatuses('T');
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, statusesAtLoss.Select(status => status.Watch.ConsecutiveLostCatchUps).ToArray());
        CollectionAssert.AreEqual(new[] { false, false, true }, losses.Select(loss => loss.RecoveryStopped).ToArray());
        CollectionAssert.AreEqual(new[] { false, false, true }, statusesAtLoss.Select(status => status.Watch.RecoveryStopped).ToArray());
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(thrown.RecoveryStopped);
        Assert.AreEqual(WatchDeduplicationTestSupport.StandardCatchUpLoss('T'), statusesAtLoss[^1].Watch.CheckpointLoss);

        Assert.AreSame(harness.BlockFor('T'), harness.Index.Root('T').DriveBlock.Block,
            "the drive keeps its third block, queryable");
        var drive = harness.DriveFor('T');
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.Watch.CatchUpState);
        Assert.AreEqual(thrown.Message, drive.Watch.FailureMessage);
        StringAssert.Contains(drive.Watch.FailureMessage, "T");
        StringAssert.Contains(drive.Watch.FailureMessage, "3");
        StringAssert.Contains(drive.Watch.FailureMessage, "12288");
        StringAssert.Contains(thrown.Message, "BrokerSession.GrowUsnJournalAsync");
        Assert.IsFalse(thrown.Message.Contains("BrokerProcess.GrowUsnJournalAsync", StringComparison.Ordinal));
        Assert.AreEqual(WatchDeduplicationTestSupport.StandardCatchUpLoss('T'), drive.Watch.CheckpointLoss);
        Assert.AreEqual(3, drive.Watch.ConsecutiveLostCatchUps);

        var refusal = await ThrowsAsync<InvalidOperationException>(
            () => harness.Index.StartWatchingAsync('T', Token));
        StringAssert.Contains(refusal.Message, "RescanAsync");
    }

    [TestMethod]
    public async Task Rescan_SuccessResetsTheCount()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Held);
        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);
        Assert.AreEqual(0, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "a later single loss retries");
        var latest = WatchDeduplicationTestSupport.CatchUpLosses(harness, 'T')[^1];
        Assert.AreEqual(1, harness.CatchUpLossStatuses('T')[^1].Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(latest.RecoveryStopped);
        Assert.AreEqual(0, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
    }

    [TestMethod]
    public async Task Rescan_ManualRescanAtTheLimit_MakesOneAttempt()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await ThrowsAsync<JournalCatchUpLostException>(() => harness.Index.RescanAsync('T', Token));
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        var thrown = await ThrowsAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.IsTrue(thrown.RecoveryStopped);
        Assert.AreEqual(4, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(harness.DriveFor('T').Watch.RecoveryStopped);

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);
        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.IsFalse(drive.Watch.RecoveryStopped);
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.Watch.CatchUpState);
        await harness.Index.StartWatchingAsync('T', Token);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the unresumable mark is cleared");
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
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));
        Assert.AreEqual(2, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps,
            "a scan that produced no block leaves the count unchanged");

        harness.ScriptScans('T', Lost('T'), Held);
        var producedBefore = harness.ProductionCount('T');
        var thrown = await ThrowsAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(thrown.RecoveryStopped);
    }

    [TestMethod]
    public async Task CatchUpLost_JournalRecreated_ReportHasNoSuggestion()
    {
        using var harness = new WatchHarness('T');
        var recreated = WatchDeduplicationTestSupport.StandardCatchUpLoss('T') with
        {
            Cause = JournalCheckpointLossCause.JournalRecreated,
            BytesBehind = null,
            SizeThatWouldHaveRetained = null
        };
        harness.ScriptScans('T', new WatchHarnessScan(recreated), new WatchHarnessScan(recreated), new WatchHarnessScan(recreated));

        var thrown = await ThrowsAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));

        Assert.AreEqual(recreated, harness.CatchUpLossStatuses('T')[^1].Watch.CheckpointLoss);
        Assert.IsTrue(thrown.RecoveryStopped);
        Assert.AreEqual(recreated, harness.DriveFor('T').Watch.CheckpointLoss);
        foreach (var message in WatchDeduplicationTestSupport.CatchUpLosses(harness, 'T').Select(loss => loss.Message))
        {
            StringAssert.DoesNotMatch(message, new System.Text.RegularExpressions.Regex("bytes|[Gg]row"));
        }
    }

    [TestMethod]
    public async Task CatchUpFailureNotProven_ScanFailsWithNoBlock_CountUnchangedNoReport()
    {
        using var harness = new WatchHarness('T');
        var original = harness.Index.Root('T').DriveBlock;
        var producerFailure = new InvalidOperationException("the broker reported an error after ScanReady");
        harness.FailNextProduction('T', producerFailure);

        var thrown = await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));

        Assert.AreSame(producerFailure, thrown.InnerException);
        StringAssert.Contains(thrown.Message, producerFailure.Message);
        var drive = harness.DriveFor('T');
        Assert.AreEqual(producerFailure.Message, drive.FailureMessage);
        Assert.AreEqual(0, drive.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.Watch.CheckpointLoss);
        Assert.AreEqual(0, WatchDeduplicationTestSupport.CatchUpLosses(harness, 'T').Length);
        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
    }

    [TestMethod]
    public async Task CatchUpLost_ProducerFailure_IsNotCounted()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        await ThrowsAsync<JournalCatchUpLostException>(() => harness.Index.RescanAsync('T', Token));

        harness.FailNextProduction('T', new OperationCanceledException("the scan was cancelled"));
        await ThrowsAsync<OperationCanceledException>(() => harness.Index.RescanAsync('T', Token));
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);

        harness.FailNextProduction('T', new InvalidOperationException("the producer failed"));
        Assert.IsTrue(harness.DriveFor('T').Watch.RecoveryStopped);
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(3, WatchDeduplicationTestSupport.CatchUpLosses(harness, 'T').Length, "no CatchUpLost fault for a producer failure");
        Assert.IsTrue(harness.DriveFor('T').Watch.RecoveryStopped);
        harness.ScriptScans('T', new WatchHarnessScan(Failure: new IOException("no block")));
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.RescanAsync('T', Token));
        Assert.IsTrue(harness.DriveFor('T').Watch.RecoveryStopped);
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.StartWatchingAsync('T', Token));
        await harness.Index.StopWatchingAsync('T', Token);
        Assert.IsTrue(harness.DriveFor('T').Watch.RecoveryStopped, "stop must not erase exhaustion");
    }

    [TestMethod]
    public async Task CatchUpLost_CountsArePerDrive()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), Lost('T', hold: heldRetry), Lost('T'));
        var rescanOfT = harness.Index.RescanAsync('T', Token);
        await heldRetry.Entered.WaitAsync(HangGuard);

        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(9, "sibling.txt", nextUsn: 900));
        Assert.AreEqual(900L, harness.Index.Root('U').DriveBlock.Block.Header.UsnNextUsn);
        harness.ScriptScans('U', Lost('U'), Held);
        harness.SetNextProducedCursor('U', WatchHarness.JournalIdentifier, 900);
        await harness.Index.RescanAsync('U', Token).WaitAsync(HangGuard);
        Assert.IsFalse(rescanOfT.IsCompleted, "T is still retrying");

        heldRetry.Release();
        await ThrowsAsync<JournalCatchUpLostException>(() => rescanOfT.WaitAsync(HangGuard));
        var driveT = harness.DriveFor('T');
        var driveU = harness.DriveFor('U');
        Assert.AreEqual(3, driveT.Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(driveT.Watch.RecoveryStopped);
        Assert.AreEqual(WatchCatchUpState.Faulted, driveT.Watch.CatchUpState);
        Assert.AreEqual(DriveState.Ready, driveU.State);
        Assert.AreEqual(0, driveU.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(driveU.Watch.RecoveryStopped);
        Assert.IsNull(driveU.Watch.FailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, driveU.Watch.CatchUpState);
    }

    [TestMethod]
    public async Task CatchUpLost_ScanCatchUpReportReplacesLiveWatchReport()
    {
        using var harness = new WatchHarness('T');
        harness.Index.HoldEveryRecovery();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(letter => letter == 'T'
            ? new JournalWindow(WatchHarness.JournalIdentifier, FirstUsn: 5000, NextUsn: 8000,
                AllocationDelta: 64, MaximumSize: 128L * 1024 * 1024)
            : null);
        await harness.Index.StartWatchingAsync('T', Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("T lost its checkpoint"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, harness.DriveFor('T').Watch.CheckpointLoss?.DetectedDuring);

        harness.ScriptScans('T', Lost('T'), Held);
        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        Assert.AreEqual(WatchDeduplicationTestSupport.StandardCatchUpLoss('T'), harness.DriveFor('T').Watch.CheckpointLoss);
    }

    /// <summary>
    ///     A healthy watched drive whose first attempt publishes a lost catch-up no longer has the
    ///     old cursor to go back to, so when its retry produces no block the rescan throws the
    ///     producer's failure and refuses the watch rather than restarting from the lost block.
    /// </summary>
    [TestMethod]
    public async Task Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var producerFailure = new IOException("the volume went away");
        harness.ScriptScans('T', Lost('T'), new WatchHarnessScan(Failure: producerFailure));

        var thrown = await ThrowsAsync<InvalidOperationException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));

        Assert.AreSame(producerFailure, thrown.InnerException);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the lost block's cursor is never started");
        var drive = harness.DriveFor('T');
        Assert.AreEqual(1, drive.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.Watch.CatchUpState);
        Assert.IsFalse(drive.Watch.RecoveryStopped, "an unresumable refusal is not exhaustion");
        StringAssert.Contains(drive.Watch.FailureMessage, "RescanAsync");
        Assert.AreEqual(WatchDeduplicationTestSupport.StandardCatchUpLoss('T'), drive.Watch.CheckpointLoss);
    }
}
