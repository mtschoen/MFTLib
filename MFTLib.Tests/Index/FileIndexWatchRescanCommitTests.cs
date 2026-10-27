using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanCommitTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task BatchedRescan_ReportsScanSuccessWhenARestartFails()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var failure = new IOException("T cannot restart");
        harness.Source.FailNextStartFor('T', failure);

        var resultT = await harness.IndexFor('T').RescanAsync(Token);
        var resultU = await harness.IndexFor('U').RescanAsync(Token);

        Assert.AreEqual(DriveOperationOutcome.Succeeded, resultT.Outcome);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, resultU.Outcome);
        var fault = harness.Faults.Single();
        Assert.AreEqual(WatchFaultKind.RescanRestart, fault.Kind);
        Assert.AreSame(failure, fault.Exception.InnerException);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('U').Watch.State);
        var stoppedT = await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
        Assert.AreSame(fault.Exception, stoppedT.Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposal_DuringRestart_RecordsNoRescanFault(bool dispose)
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var starting = harness.TrackGate();
        harness.Source.HoldStartFor('T', starting, observeToken: true);
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await starting.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var stopping = dispose
            ? harness.Index.DisposeAsync().AsTask()
            : harness.IndexFor('T').StopWatchingAsync(Token);

        await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => rescan);
        await stopping.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsEmpty(harness.Faults);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedProduction_KeepsTheWatchAndPendingCatchUp(bool cancel)
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        var original = harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock;
        var caughtUp = harness.IndexFor('T').WaitForCatchUpAsync(Token);
        var production = harness.HoldNextProduction('T');
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (!cancel)
        {
            harness.FailNextProduction('T', new IOException("production failed"));
        }

        var rescan = harness.IndexFor('T').RescanAsync(cancellation.Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, handle.DisposeCount, "production must keep the current watch running");
        Assert.IsFalse(caughtUp.IsCompleted, "production must keep the pending catch-up attached");
        await handle.Publish(WatchHarness.Batch(9, "during.txt", nextUsn: 700));
        Assert.AreEqual(700L, original.Block.Header.UsnNextUsn);
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => rescan);
        }
        else
        {
            production.Release();
            await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => rescan);
        }

        Assert.AreSame(original, harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock);
        Assert.AreSame(handle, harness.Source.WatchFor('T'));
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        Assert.IsFalse(caughtUp.IsCompleted);
        await handle.Publish(new DriveCaughtUp());
        await caughtUp.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartFailure_IsARescanFaultAndStopReportsItOnceAsFailedResult(bool alreadyFaulted)
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        if (alreadyFaulted)
        {
            harness.Source.WatchFor('T').LoseChannel(new IOException("old channel failed"));
            await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        }

        var failure = new IOException("new start failed");
        harness.Source.FailNextStartFor('T', failure);
        harness.SetNextProducedCursor('T', 13, 9000);
        var original = harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock;
        await harness.IndexFor('T').RescanAsync(Token);

        Assert.AreNotSame(original, harness.IndexFor('T').Root(TestContext.CancellationToken).DriveBlock);
        var fault = harness.Faults.Single(item => item.Kind == WatchFaultKind.RescanRestart);
        Assert.AreSame(failure, fault.Exception.InnerException);
        Assert.Contains("rescan replaced", fault.Exception.Message);
        Assert.Contains("watch could not be started", fault.Exception.Message);
        Assert.AreEqual(fault.Exception.Message, harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(2, harness.ProductionCount('T'));
        var stopped = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreSame(fault.Exception, stopped);
        await DriveResultAssertions.NotApplicableAsync(() => harness.IndexFor('T').StopWatchingAsync(Token));
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(9, "sibling.txt"));
        await harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FaultDuringProduction_RecoversOnlyWhenTheScanFails(bool scanFails)
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var production = harness.HoldNextProduction('T');
        if (scanFails)
        {
            harness.FailNextProduction('T', new IOException("scan failed"));
        }

        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var handle = harness.Source.WatchFor('T');
        Assert.AreEqual(0, handle.DisposeCount, "a fault during production still belongs to the current watch");
        handle.FailDrive(new IOException("watch failed during production"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        production.Release();
        if (scanFails)
        {
            await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => rescan);
        }
        else
        {
            await rescan;
        }

        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(scanFails ? 3 : 2, harness.ProductionCount('T'));
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }
}
