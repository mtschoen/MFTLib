using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanCommitTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchedRescan_ReportsScanSuccessWhenARestartFails(bool allDrives)
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(Token);
        var failure = new IOException("T cannot restart");
        harness.Source.FailStartFor('T', failure);

        var results = allDrives
            ? await harness.Index.RescanAsync(Token)
            : await harness.Index.RescanAsync(['T', 'U'], Token);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
        var fault = harness.Faults.Single();
        Assert.AreEqual(WatchFaultKind.RescanRestart, fault.Kind);
        Assert.AreSame(failure, fault.Exception.InnerException);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('U').WatchCatchUp);
        var stopped = await harness.Index.StopWatchingAsync(Token);
        Assert.AreSame(fault.Exception, stopped.Single(result => result.DriveLetter == 'T').Failure);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposal_DuringRestart_RecordsNoRescanFault(bool dispose)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var starting = harness.TrackGate();
        harness.Source.HoldStartFor('T', starting, observeToken: true);
        var rescan = harness.Index.RescanAsync('T', Token);
        await starting.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);

        var stopping = dispose
            ? harness.Index.DisposeAsync().AsTask()
            : harness.Index.StopWatchingAsync('T', Token);

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => rescan);
        await stopping.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(0, harness.Faults.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedProduction_KeepsTheWatchAndPendingCatchUp(bool cancel)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        var original = harness.Index.Root('T').DriveBlock;
        var caughtUp = harness.Index.WaitForCatchUpAsync('T', Token);
        var production = harness.HoldNextProduction('T');
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (!cancel)
        {
            harness.FailNextProduction('T', new IOException("production failed"));
        }

        var rescan = harness.Index.RescanAsync('T', cancellation.Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(0, handle.DisposeCount, "production must keep the current watch running");
        Assert.IsFalse(caughtUp.IsCompleted, "production must keep the pending catch-up attached");
        await handle.Publish(WatchHarness.Batch(9, "during.txt", nextUsn: 700));
        Assert.AreEqual(700L, original.Block.Header.UsnNextUsn);
        if (cancel)
        {
            await cancellation.CancelAsync();
            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => rescan);
        }
        else
        {
            production.Release();
            await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => rescan);
        }

        Assert.AreSame(original, harness.Index.Root('T').DriveBlock);
        Assert.AreSame(handle, harness.Source.HandleFor('T'));
        Assert.AreEqual(1, harness.Source.StartsFor('T').Count);
        Assert.IsFalse(caughtUp.IsCompleted);
        await handle.Publish(new DriveCaughtUp());
        await caughtUp.WaitAsync(FakeIndexWatchSource.HangGuard);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartFailure_IsARescanFaultAndStopRethrowsItOnce(bool alreadyFaulted)
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        if (alreadyFaulted)
        {
            harness.Source.HandleFor('T').LoseChannel(new IOException("old channel failed"));
            await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        }

        var failure = new IOException("new start failed");
        harness.Source.FailStartFor('T', failure);
        harness.SetNextProducedCursor('T', 13, 9000);
        var original = harness.Index.Root('T').DriveBlock;
        await harness.Index.RescanAsync('T', Token);

        Assert.AreNotSame(original, harness.Index.Root('T').DriveBlock);
        var fault = harness.Faults.Single(item => item.Kind == WatchFaultKind.RescanRestart);
        Assert.AreSame(failure, fault.Exception.InnerException);
        StringAssert.Contains(fault.Exception.Message, "rescan replaced");
        StringAssert.Contains(fault.Exception.Message, "watch could not be started");
        Assert.AreEqual(fault.Exception.Message, harness.DriveFor('T').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(0, harness.RecoveryCount('T'));
        Assert.AreEqual(2, harness.ProductionCount('T'));
        var stopped = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreSame(fault.Exception, stopped);
        var secondStop = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreNotSame(fault.Exception, secondStop);
        await harness.Source.HandleFor('U').Publish(WatchHarness.Batch(9, "sibling.txt"));
        await harness.Index.StartWatchingAsync('T', Token);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FaultDuringProduction_RecoversOnlyWhenTheScanFails(bool scanFails)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var production = harness.HoldNextProduction('T');
        if (scanFails)
        {
            harness.FailNextProduction('T', new IOException("scan failed"));
        }

        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        var handle = harness.Source.HandleFor('T');
        Assert.AreEqual(0, handle.DisposeCount, "a fault during production still belongs to the current watch");
        handle.FailDrive(new IOException("watch failed during production"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        production.Release();
        if (scanFails)
        {
            await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => rescan);
        }
        else
        {
            await rescan;
        }

        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(scanFails ? 3 : 2, harness.ProductionCount('T'));
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        await harness.Index.StopWatchingAsync('T', Token);
    }
}
