using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     The batched entry points (spec section 3, "Batched-call contract"): one result per requested
///     drive in the order given, the per-drive operations concurrent, the call returning only after
///     every one settled, and a throw only for caller errors and cancellation. Ordering is proven
///     with <see cref="TestGate" /> holds and scripted producers, never with time.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexBatchedOperationTests
{
    [ThreadStatic] static bool _settlingPumpFault;

    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static JournalCheckpointLoss Loss(char driveLetter) => new JournalCheckpointLoss(driveLetter, JournalCheckpointLossDetection.ScanCatchUp, JournalCheckpointLossCause.CheckpointTrimmed,
        4096, 32768)
    {
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    static WatchHarnessScan Lost(char driveLetter) => new(Loss(driveLetter));

    static char[] Letters(IReadOnlyList<DriveOperationResult> results) =>
        [.. results.Select(result => result.DriveLetter)];

    [TestMethod]
    public async Task BatchedStart_PartialFailure_ReportsPerDrive_DoesNotThrow()
    {
        using var harness = WatchHarness.WithBlocklessDrives(['V'], 'T', 'U', 'V');
        var failure = new IOException("the broker refused U");
        harness.Source.FailNextStartFor('U', failure);

        var results = await harness.Index.StartWatchingAsync(['T', 'U', 'V'], Token).WaitAsync(HangGuard);

        CollectionAssert.AreEqual(new[] { 'T', 'U', 'V' }, Letters(results));
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[0].Outcome);
        Assert.IsNull(results[0].Failure);
        Assert.AreEqual(DriveOperationOutcome.Failed, results[1].Outcome);
        Assert.AreSame(failure, results[1].Failure);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results[2].Outcome);
        Assert.IsNull(results[2].Failure);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(0, harness.Source.TargetsFor('V').Count, "a drive with no block never reaches the source");
        Assert.AreEqual(0, harness.Faults.Count, "a failed source start raises no WatchFaulted");
    }

    [TestMethod]
    public async Task BatchedStart_CancelledWhileGated_ThrowsOnlyAfterEverySettles_NoHandlePublished()
    {
        using var harness = new WatchHarness();
        var heldStart = harness.TrackGate();
        harness.Source.HoldStartFor('U', heldStart, observeToken: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var batch = harness.Index.StartWatchingAsync(['U'], cancellation.Token);
        try
        {
            await heldStart.Entered.WaitAsync(HangGuard);

            await cancellation.CancelAsync().WaitAsync(HangGuard);

            await ThrowsAsync<OperationCanceledException>(() => batch.WaitAsync(HangGuard));
        }
        finally
        {
            heldStart.Release();
        }

        Assert.AreEqual(1, harness.Source.TargetsFor('U').Count, "U's start reached the source and was cancelled there");
        Assert.AreEqual(0, harness.Source.Watches.Count, "no handle was published");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('U').WatchCatchUp,
            "a start cancelled in flight leaves the drive with no running watch");
    }

    [TestMethod]
    public async Task BatchedStart_CancelledWhileAnotherStartIsInFlight_WaitsForItBeforeThrowing()
    {
        using var harness = new WatchHarness();
        var heldScanT = harness.HoldNextProduction('T');
        var heldStartV = harness.TrackGate();
        harness.Source.HoldStartFor('V', heldStartV, observeToken: false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var rescanT = harness.Index.RescanAsync('T', Token);
        try
        {
            await heldScanT.Entered.WaitAsync(HangGuard);
            var batch = harness.Index.StartWatchingAsync(['T', 'V'], cancellation.Token);
            await heldStartV.Entered.WaitAsync(HangGuard);

            await cancellation.CancelAsync().WaitAsync(HangGuard);

            Assert.IsFalse(batch.IsCompleted, "V's start ignores the token, so the call has not settled");
            heldStartV.Release();
            await ThrowsAsync<OperationCanceledException>(() => batch.WaitAsync(HangGuard));
        }
        finally
        {
            heldStartV.Release();
            heldScanT.Release();
        }

        await rescanT.WaitAsync(HangGuard);
        Assert.AreEqual(0, harness.Source.TargetsFor('T').Count, "T's start never left the lifecycle gate");
    }

    [TestMethod]
    public async Task Batched_CancelledTokenWithNothingToDo_StillThrows()
    {
        using var harness = new WatchHarness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().WaitAsync(HangGuard);
        var index = harness.Index;
        var none = Array.Empty<char>();

        await ThrowsAsync<OperationCanceledException>(() => index.StartWatchingAsync(none, cancelled.Token).WaitAsync(HangGuard));
        await ThrowsAsync<OperationCanceledException>(() => index.StopWatchingAsync(none, cancelled.Token).WaitAsync(HangGuard));
        await ThrowsAsync<OperationCanceledException>(() => index.RescanAsync(none, cancelled.Token).WaitAsync(HangGuard));
        await ThrowsAsync<OperationCanceledException>(() => index.WaitForCatchUpAsync(none, cancelled.Token).WaitAsync(HangGuard));
        await ThrowsAsync<OperationCanceledException>(() => index.StopWatchingAsync(['V'], cancelled.Token).WaitAsync(HangGuard));
        await ThrowsAsync<OperationCanceledException>(() => index.WaitForCatchUpAsync(['V'], cancelled.Token).WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task Batched_DuplicateLetter_ThrowsArgumentBeforeStarting()
    {
        using var harness = new WatchHarness();

        await ThrowsAsync<ArgumentException>(() => harness.Index.StartWatchingAsync(['T', 'U', 't'], Token));
        await ThrowsAsync<ArgumentException>(() => harness.Index.StopWatchingAsync(['U', 'U'], Token));
        await ThrowsAsync<ArgumentException>(() => harness.Index.RescanAsync(['T', 'T'], Token));
        await ThrowsAsync<ArgumentException>(() => harness.Index.WaitForCatchUpAsync(['V', 'v'], Token));

        Assert.AreEqual(0, harness.Source.Starts.Count);
        Assert.AreEqual(1, harness.ProductionCount('T'), "only the open scanned");
        Assert.AreEqual(1, harness.ProductionCount('U'), "only the open scanned");
    }

    [TestMethod]
    public async Task Batched_UnknownLetter_Throws()
    {
        using var harness = new WatchHarness();

        await ThrowsAsync<ArgumentException>(() => harness.Index.StartWatchingAsync(['T', 'X'], Token));
        await ThrowsAsync<ArgumentException>(() => harness.Index.StopWatchingAsync(['X'], Token));
        await ThrowsAsync<ArgumentException>(() => harness.Index.RescanAsync(['U', 'X'], Token));
        await ThrowsAsync<ArgumentException>(() => harness.Index.WaitForCatchUpAsync(['X'], Token));

        Assert.AreEqual(0, harness.Source.Starts.Count, "the list is validated before any drive starts");
        Assert.AreEqual(1, harness.ProductionCount('U'), "only the open scanned");
    }

    [TestMethod]
    public async Task Batched_Null_Throws()
    {
        using var harness = new WatchHarness();
        var index = harness.Index;

        await ThrowsAsync<ArgumentNullException>(() => index.StartWatchingAsync(null!, Token));
        await ThrowsAsync<ArgumentNullException>(() => index.StopWatchingAsync(null!, Token));
        await ThrowsAsync<ArgumentNullException>(() => index.RescanAsync(null!, Token));
        await ThrowsAsync<ArgumentNullException>(() => index.WaitForCatchUpAsync(null!, Token));
    }

    [TestMethod]
    public async Task Batched_Disposed_ThrowsObjectDisposed()
    {
        using var harness = new WatchHarness();
        var index = harness.Index;
        await index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await ThrowsAsync<ObjectDisposedException>(() => index.StartWatchingAsync(['T'], Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.StopWatchingAsync(['T'], Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.RescanAsync(['T'], Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.WaitForCatchUpAsync(['T'], Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.StartWatchingAsync(Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.StopWatchingAsync(Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.RescanAsync(Token));
        await ThrowsAsync<ObjectDisposedException>(() => index.WaitForCatchUpAsync(Token));
    }

    [TestMethod]
    public async Task NoListForm_CoversDrivesInOptionsOrder()
    {
        using var harness = new WatchHarness('V', 'T', 'U');
        var expected = new[] { 'V', 'T', 'U' };

        var started = await harness.Index.StartWatchingAsync(Token).WaitAsync(HangGuard);
        CollectionAssert.AreEqual(expected, Letters(started));
        Assert.IsTrue(started.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

        var waiting = harness.Index.WaitForCatchUpAsync(Token);
        foreach (var letter in expected)
        {
            await harness.Source.WatchFor(letter).Publish(new DriveCaughtUp());
        }

        var caughtUp = await waiting.WaitAsync(HangGuard);
        CollectionAssert.AreEqual(expected, Letters(caughtUp));
        Assert.IsTrue(caughtUp.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

        var rescanned = await harness.Index.RescanAsync(Token).WaitAsync(HangGuard);
        CollectionAssert.AreEqual(expected, Letters(rescanned));
        Assert.IsTrue(rescanned.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

        var stopped = await harness.Index.StopWatchingAsync(Token).WaitAsync(HangGuard);
        CollectionAssert.AreEqual(expected, Letters(stopped));
        Assert.IsTrue(stopped.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task BatchedStop_ReturnsFaultAsFailed()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T', 'U'], Token).WaitAsync(HangGuard);
        var faulted = harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        var fault = await faulted.WaitAsync(HangGuard);

        var results = await harness.Index.StopWatchingAsync(['T', 'U', 'V'], Token).WaitAsync(HangGuard);

        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome);
        Assert.AreSame(fault.Exception, results[0].Failure, "stop carries the fault that had ended T's watch");
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[1].Outcome);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results[2].Outcome, "V was never watching");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task BatchedWait_ReturnsPerDrive()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T', 'U'], Token).WaitAsync(HangGuard);
        var wait = harness.Index.WaitForCatchUpAsync(['T', 'U', 'V'], Token);

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.IsFalse(wait.IsCompleted, "U is still catching up");
        harness.Source.WatchFor('U').FailDrive(new IOException("U's journal wrapped"));

        var results = await wait.WaitAsync(HangGuard);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[0].Outcome);
        Assert.AreEqual(DriveOperationOutcome.Failed, results[1].Outcome);
        Assert.IsInstanceOfType<DriveWatchFaultException>(results[1].Failure);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results[2].Outcome, "V is not being watched");
    }

    [TestMethod]
    public async Task BatchedWait_SettledByPumpFault_ContinuationNotInline()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync(['T', 'U'], Token).WaitAsync(HangGuard);
        harness.Index.PumpFaultSettlementWrapperForTest = settle =>
        {
            _settlingPumpFault = true;
            try
            {
                settle();
            }
            finally
            {
                _settlingPumpFault = false;
            }
        };
        var waiter = WaitThenStopOtherDriveAsync(harness.Index);

        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));

        Assert.IsFalse(await waiter.WaitAsync(HangGuard), "the waiter's continuation ran on the settling stack");
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('U').WatchCatchUp);
        return;

        async Task<bool> WaitThenStopOtherDriveAsync(FileIndex index)
        {
            var results = await index.WaitForCatchUpAsync(['T'], Token).WaitAsync(HangGuard);
            Assert.AreEqual(DriveOperationOutcome.Failed, results.Single().Outcome);
            var observedWhileSettling = _settlingPumpFault;
            await index.StopWatchingAsync('U', Token).WaitAsync(HangGuard);
            return observedWhileSettling;
        }
    }

    [TestMethod]
    public async Task BatchedRescan_OneDriveStopsAfterThreeLostCatchUps_ReportsFailedWithMessage_OthersSucceed()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        var results = await harness.Index.RescanAsync(['T', 'U'], Token).WaitAsync(HangGuard);

        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome);
        var lost = (JournalCatchUpLostException)results[0].Failure!;
        Assert.IsTrue(lost.RecoveryStopped);
        Assert.AreEqual(3, harness.DriveFor('T').ConsecutiveLostCatchUps);
        StringAssert.Contains(lost.Message, "12288", "the message carries the journal size to grow to");
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[1].Outcome);
        Assert.AreEqual(DriveState.Ready, harness.DriveFor('U').State);
    }

    [TestMethod]
    public async Task SingleRescan_CatchUpStop_ThrowsJournalCatchUpLostException()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        var thrown = await ThrowsAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));

        Assert.IsTrue(thrown.RecoveryStopped);
        Assert.AreEqual(3, harness.DriveFor('T').ConsecutiveLostCatchUps);
        StringAssert.Contains(thrown.Message, "12288");
    }

    [TestMethod]
    public async Task SingleRescan_ProducerReturnsNoBlock_ThrowsWithFailureMessage()
    {
        using var harness = new WatchHarness('T');
        var failure = new IOException("the scan could not read the volume");
        harness.FailNextProduction('T', failure);

        var thrown = await ThrowsAsync<InvalidOperationException>(
            () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));

        StringAssert.Contains(thrown.Message, "the scan could not read the volume");
        Assert.AreSame(failure, thrown.InnerException);
        Assert.AreEqual("the scan could not read the volume", harness.DriveFor('T').MftProducerFailureMessage);
    }
}
