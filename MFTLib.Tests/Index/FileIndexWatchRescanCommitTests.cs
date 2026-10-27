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
        await harness.Index.StartWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        var failure = new IOException("T cannot restart");
        harness.Source.FailNextStartFor('T', failure);

        var results = await harness.Index.RescanAsync([new VolumeKey('T'), new VolumeKey('U')], Token);

        Assert.HasCount(2, results);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
        var fault = harness.Faults.Single();
        Assert.AreEqual(WatchFaultKind.RescanRestart, fault.Kind);
        Assert.AreSame(failure, fault.Exception.InnerException);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('U').Watch.State);
        var stopped = await harness.Index.StopWatchingAsync(harness.Index.Drives.Select(drive => drive.Drive), Token);
        Assert.AreSame(fault.Exception, stopped.Single(result => result.Drive == new VolumeKey('T')).Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposal_DuringRestart_RecordsNoRescanFault(bool dispose)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync([new VolumeKey('T')], Token);
        var starting = harness.TrackGate();
        harness.Source.HoldStartFor('T', starting, observeToken: true);
        var rescan = harness.Index.RescanAsync([new VolumeKey('T')], Token);
        await starting.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        var stopping = dispose
            ? harness.Index.DisposeAsync().AsTask()
            : harness.Index.StopWatchingAsync([new VolumeKey('T')], Token);

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
        await harness.Index.StartWatchingAsync([new VolumeKey('T')], Token);
        var handle = harness.Source.WatchFor('T');
        var original = harness.Index.Root(new VolumeKey('T'), TestContext.CancellationToken).DriveBlock;
        var caughtUp = harness.Index.WaitForCatchUpAsync([new VolumeKey('T')], Token);
        var production = harness.HoldNextProduction('T');
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (!cancel)
        {
            harness.FailNextProduction('T', new IOException("production failed"));
        }

        var rescan = harness.Index.RescanAsync([new VolumeKey('T')], cancellation.Token);
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

        Assert.AreSame(original, harness.Index.Root(new VolumeKey('T'), TestContext.CancellationToken).DriveBlock);
        Assert.AreSame(handle, harness.Source.WatchFor('T'));
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        Assert.IsFalse(caughtUp.IsCompleted);
        await handle.Publish(new DriveCaughtUp());
        await caughtUp.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Index.StopWatchingAsync([new VolumeKey('T')], Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartFailure_IsARescanFaultAndStopReportsItOnceAsFailedResult(bool alreadyFaulted)
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync([new VolumeKey('T')], Token);
        await harness.Index.StartWatchingAsync([new VolumeKey('U')], Token);
        if (alreadyFaulted)
        {
            harness.Source.WatchFor('T').LoseChannel(new IOException("old channel failed"));
            await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        }

        var failure = new IOException("new start failed");
        harness.Source.FailNextStartFor('T', failure);
        harness.SetNextProducedCursor('T', 13, 9000);
        var original = harness.Index.Root(new VolumeKey('T'), TestContext.CancellationToken).DriveBlock;
        await harness.Index.RescanAsync([new VolumeKey('T')], Token);

        Assert.AreNotSame(original, harness.Index.Root(new VolumeKey('T'), TestContext.CancellationToken).DriveBlock);
        var fault = harness.Faults.Single(item => item.Kind == WatchFaultKind.RescanRestart);
        Assert.AreSame(failure, fault.Exception.InnerException);
        Assert.Contains("rescan replaced", fault.Exception.Message);
        Assert.Contains("watch could not be started", fault.Exception.Message);
        Assert.AreEqual(fault.Exception.Message, harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(2, harness.ProductionCount('T'));
        var stopped = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync([new VolumeKey('T')], Token));
        Assert.AreSame(fault.Exception, stopped);
        await DriveResultAssertions.NotApplicableAsync(() => harness.Index.StopWatchingAsync([new VolumeKey('T')], Token));
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(9, "sibling.txt"));
        await harness.Index.StartWatchingAsync([new VolumeKey('T')], Token);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.Index.StopWatchingAsync([new VolumeKey('T')], Token);
        await harness.Index.StopWatchingAsync([new VolumeKey('U')], Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FaultDuringProduction_RecoversOnlyWhenTheScanFails(bool scanFails)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync([new VolumeKey('T')], Token);
        var production = harness.HoldNextProduction('T');
        if (scanFails)
        {
            harness.FailNextProduction('T', new IOException("scan failed"));
        }

        var rescan = harness.Index.RescanAsync([new VolumeKey('T')], Token);
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
        await harness.Index.StopWatchingAsync([new VolumeKey('T')], Token);
    }
}
