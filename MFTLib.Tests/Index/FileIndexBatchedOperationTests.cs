using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

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

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    static RootKey[] Letters(IReadOnlyList<DriveOperationResult> results) =>
        [.. results.Select(result => result.Drive)];

    [TestMethod]
    public async Task BatchedStart_PartialFailure_ReportsPerDrive_DoesNotThrow()
    {
        using var harness = WatchHarness.WithBlocklessDrives(['V'], 'T', 'U', 'V');
        var failure = new IOException("the broker refused U");
        harness.Source.FailNextStartFor('U', failure);

        var results = await harness.Index.StartWatchingAsync([new VolumeKey('T'), new VolumeKey('U'), new VolumeKey('V')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSequenceEqual([new VolumeKey('T'), new VolumeKey('U'), new VolumeKey('V')], Letters(results));
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[0].Outcome);
        Assert.IsNull(results[0].Failure);
        Assert.AreEqual(DriveOperationOutcome.Failed, results[1].Outcome);
        Assert.AreSame(failure, results[1].Failure);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results[2].Outcome);
        Assert.IsNull(results[2].Failure);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.IsEmpty(harness.Source.TargetsFor('V'), "a drive with no block never reaches the source");
        Assert.IsEmpty(harness.Faults, "a failed source start raises no WatchFaulted");
    }

    [TestMethod]
    public async Task BatchedStart_CancelledWhileGated_ThrowsOnlyAfterEverySettles_NoHandlePublished()
    {
        using var harness = new WatchHarness();
        var heldStart = harness.TrackGate();
        harness.Source.HoldStartFor('U', heldStart, observeToken: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var batch = harness.Index.StartWatchingAsync([new VolumeKey('U')], cancellation.Token);
        try
        {
            await heldStart.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

            await cancellation.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);

            await Assert.ThrowsAsync<OperationCanceledException>(() => batch.WaitAsync(HangGuard, TestContext.CancellationToken));
        }
        finally
        {
            heldStart.Release();
        }

        Assert.HasCount(1, harness.Source.TargetsFor('U'), "U's start reached the source and was cancelled there");
        Assert.IsEmpty(harness.Source.Watches, "no handle was published");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('U').Watch.State,
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
        var rescanT = harness.Index.RescanAsync([new VolumeKey('T')], Token);
        try
        {
            await heldScanT.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            var batch = harness.Index.StartWatchingAsync([new VolumeKey('T'), new VolumeKey('V')], cancellation.Token);
            await heldStartV.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

            await cancellation.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);

            Assert.IsFalse(batch.IsCompleted, "V's start ignores the token, so the call has not settled");
            heldStartV.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => batch.WaitAsync(HangGuard, TestContext.CancellationToken));
        }
        finally
        {
            heldStartV.Release();
            heldScanT.Release();
        }

        await rescanT.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsEmpty(harness.Source.TargetsFor('T'), "T's start never left the lifecycle gate");
    }

    [TestMethod]
    public async Task Batched_CancelledTokenWithNothingToDo_StillThrows()
    {
        using var harness = new WatchHarness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().WaitAsync(HangGuard, TestContext.CancellationToken);
        var index = harness.Index;
        var none = Array.Empty<char>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => index.StartWatchingAsync(none.Select(letter => (RootKey)new VolumeKey(letter)), cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.StopWatchingAsync(none.Select(letter => (RootKey)new VolumeKey(letter)), cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.RescanAsync(none.Select(letter => (RootKey)new VolumeKey(letter)), cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.WaitForCatchUpAsync(none.Select(letter => (RootKey)new VolumeKey(letter)), cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.StopWatchingAsync([new VolumeKey('V')], cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.WaitForCatchUpAsync([new VolumeKey('V')], cancelled.Token).WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Batched_DuplicateLetter_ThrowsArgumentBeforeStarting()
    {
        using var harness = new WatchHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.StartWatchingAsync(
            [new VolumeKey('T'), new VolumeKey('U'), new VolumeKey('t')], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.StopWatchingAsync(
            [new VolumeKey('U'), new VolumeKey('U')], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.RescanAsync(
            [new VolumeKey('T'), new VolumeKey('T')], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.WaitForCatchUpAsync(
            [new VolumeKey('V'), new VolumeKey('v')], Token));

        Assert.IsEmpty(harness.Source.Starts);
        Assert.AreEqual(1, harness.ProductionCount('T'), "only the open scanned");
        Assert.AreEqual(1, harness.ProductionCount('U'), "only the open scanned");
    }

    [TestMethod]
    public async Task Batched_UnknownLetter_Throws()
    {
        using var harness = new WatchHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.StartWatchingAsync(
            [new VolumeKey('T'), new VolumeKey('X')], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.StopWatchingAsync([new VolumeKey('X')], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.RescanAsync(
            [new VolumeKey('U'), new VolumeKey('X')], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Index.WaitForCatchUpAsync([new VolumeKey('X')], Token));

        Assert.IsEmpty(harness.Source.Starts, "the list is validated before any drive starts");
        Assert.AreEqual(1, harness.ProductionCount('U'), "only the open scanned");
    }

    [TestMethod]
    public async Task Batched_Null_Throws()
    {
        using var harness = new WatchHarness();
        var index = harness.Index;

        await Assert.ThrowsAsync<ArgumentNullException>(() => index.StartWatchingAsync(null!, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.StopWatchingAsync(null!, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.RescanAsync(null!, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.WaitForCatchUpAsync(null!, Token));
    }

    [TestMethod]
    public async Task Batched_Disposed_ThrowsObjectDisposed()
    {
        using var harness = new WatchHarness();
        var index = harness.Index;
        await index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.StartWatchingAsync([new VolumeKey('T')], Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.StopWatchingAsync([new VolumeKey('T')], Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.RescanAsync([new VolumeKey('T')], Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.WaitForCatchUpAsync([new VolumeKey('T')], Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.StartWatchingAsync(index.Drives.Select(drive => drive.Drive), Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.StopWatchingAsync(index.Drives.Select(drive => drive.Drive), Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.WaitForCatchUpAsync(index.Drives.Select(drive => drive.Drive), Token));
    }

    [TestMethod]
    public async Task NoListForm_CoversDrivesInOptionsOrder()
    {
        using var harness = new WatchHarness('V', 'T', 'U');
        RootKey[] expected = [new VolumeKey('V'), new VolumeKey('T'), new VolumeKey('U')];

        var started = await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreSequenceEqual(expected, Letters(started));
        Assert.IsTrue(started.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

        var waiting = harness.Index.WaitForCatchUpAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        foreach (var letter in expected)
        {
            Assert.IsTrue(letter.TryGetVolume(out var volume));
            await harness.Source.WatchFor(volume).Publish(new DriveCaughtUp());
        }

        var caughtUp = await waiting.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreSequenceEqual(expected, Letters(caughtUp));
        Assert.IsTrue(caughtUp.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

        var rescanned = await harness.Index.RescanAsync([.. harness.Index.Drives.Select(drive => drive.Drive)], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreSequenceEqual(expected, Letters(rescanned));
        Assert.IsTrue(rescanned.All(result => result.Outcome == DriveOperationOutcome.Succeeded));

        var stopped = await harness.Index.StopWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreSequenceEqual(expected, Letters(stopped));
        Assert.IsTrue(stopped.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task BatchedStop_ReturnsFaultAsFailed()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync([new VolumeKey('T'), new VolumeKey('U')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var faulted = harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        harness.Source.WatchFor('T').FailDrive(new IOException("the volume went away"));
        var fault = await faulted.WaitAsync(HangGuard, TestContext.CancellationToken);

        var results = await harness.Index.StopWatchingAsync([new VolumeKey('T'), new VolumeKey('U'), new VolumeKey('V')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome);
        Assert.AreSame(fault.Exception, results[0].Failure, "stop carries the fault that had ended T's watch");
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[1].Outcome);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results[2].Outcome, "V was never watching");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task BatchedWait_ReturnsPerDrive()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync([new VolumeKey('T'), new VolumeKey('U')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var wait = harness.Index.WaitForCatchUpAsync([new VolumeKey('T'), new VolumeKey('U'), new VolumeKey('V')], Token);

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.IsFalse(wait.IsCompleted, "U is still catching up");
        harness.Source.WatchFor('U').FailDrive(new IOException("U's journal wrapped"));

        var results = await wait.WaitAsync(HangGuard, TestContext.CancellationToken);
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
        await harness.Index.StartWatchingAsync([new VolumeKey('T'), new VolumeKey('U')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        harness.Index.Hooks.PumpFaultSettlementWrapper = settle =>
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

        Assert.IsFalse(await waiter.WaitAsync(HangGuard, TestContext.CancellationToken), "the waiter's continuation ran on the settling stack");
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('U').Watch.State);
        return;

        async Task<bool> WaitThenStopOtherDriveAsync(FileIndex index)
        {
            var results = await index.WaitForCatchUpAsync([new VolumeKey('T')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(DriveOperationOutcome.Failed, results.Single().Outcome);
            var observedWhileSettling = _settlingPumpFault;
            await index.StopWatchingAsync([new VolumeKey('U')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);
            return observedWhileSettling;
        }
    }

    [TestMethod]
    public async Task BatchedRescan_OneDriveStopsAfterThreeLostCatchUps_ReportsFailedWithMessage_OthersSucceed()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        var results = await harness.Index.RescanAsync([new VolumeKey('T'), new VolumeKey('U')], Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome);
        var lost = (JournalCatchUpLostException)results[0].Failure!;
        Assert.IsTrue(lost.IsRecoveryStopped);
        Assert.IsTrue(harness.DriveFor('T').Watch.IsRecoveryStopped);
        Assert.IsFalse(harness.DriveFor('U').Watch.IsRecoveryStopped);
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.Contains("12288", lost.Message, "the message carries the journal size to grow to");
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results[1].Outcome);
        Assert.AreEqual(DriveState.Ready, harness.DriveFor('U').State);
    }

    [TestMethod]
    public async Task SingleRescan_CatchUpStop_ReportsJournalCatchUpLostFailure()
    {
        using var harness = new WatchHarness('T');
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        var failure = await DriveResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.Index.RescanAsync([new VolumeKey('T')], Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.IsTrue(failure.IsRecoveryStopped);
        Assert.AreEqual(3, harness.DriveFor('T').Watch.ConsecutiveLostCatchUps);
        Assert.Contains("12288", failure.Message);
    }

    [TestMethod]
    public async Task SingleRescan_ProducerReturnsNoBlock_ReportsFailedResultWithFailureMessage()
    {
        using var harness = new WatchHarness('T');
        var producerFailure = new IOException("the scan could not read the volume");
        harness.FailNextProduction('T', producerFailure);

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.RescanAsync([new VolumeKey('T')], Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("the scan could not read the volume", failure.Message);
        Assert.AreSame(producerFailure, failure.InnerException);
        Assert.AreEqual("the scan could not read the volume", harness.DriveFor('T').Failure?.Message);
    }
}
