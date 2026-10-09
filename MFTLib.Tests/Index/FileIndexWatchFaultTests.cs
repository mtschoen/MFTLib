using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The per-drive fault lane. Every fault here is injected through a drive's scripted handle or a
///     throwing subscriber, so none of these needs a broker, a volume, or Windows.
/// </summary>
[TestClass]
public class FileIndexWatchFaultTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task ApplyFailure_IsReportedAsAnApplyFaultAndIsolatesTheDrive()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var frozenCursor = harness.BlockFor('T').Header.UsnNextUsn;

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, NextUsn: 5000));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.IsInstanceOfType<ArgumentNullException>(fault.Exception);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage, "the recovery replaced the faulted watch");
        Assert.AreEqual(DriveState.Ready, harness.DriveFor('T').State);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').Watch.CatchUpState);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(1, harness.Source.TargetsFor('U').Count);
        Assert.IsNull(harness.DriveFor('U').Watch.FailureMessage);
        Assert.AreEqual(frozenCursor, harness.BlockFor('T').Header.UsnNextUsn, "the rejected batch advanced nothing");
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.AreEqual(1, harness.Changes.Count);
        Assert.AreEqual("u.txt", harness.Changes.Single().Entry.Name);

        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task DriveFailure_IsReportedAsADriveFaultCarryingTheDriveLetter()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var wrapped = new IOException("journal wrapped");
        var frozenCursor = harness.BlockFor('T').Header.UsnNextUsn;

        harness.Source.WatchFor('T').FailDrive(wrapped);
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));

        Assert.AreEqual(1, harness.Faults.Count);
        var driveFault = (DriveWatchFaultException)fault.Exception;
        Assert.AreEqual('T', driveFault.DriveLetter);
        Assert.AreSame(wrapped, driveFault.InnerException);
        Assert.AreEqual("journal wrapped", harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(DriveState.Ready, harness.DriveFor('T').State);
        Assert.AreEqual(frozenCursor, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);

        var thrown = await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreSame(driveFault, thrown);
    }

    [TestMethod]
    public async Task ChannelLoss_EndsOnlyThatDrivesPump_AndNamesTheDrive()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var lost = new IOException("the broker died");

        harness.Source.WatchFor('T').LoseChannel(lost);
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual('T', fault.DriveLetter);
        Assert.AreSame(lost, fault.Exception);
        Assert.AreEqual("the broker died", harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').Watch.CatchUpState);
        Assert.IsNull(harness.DriveFor('U').Watch.FailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('U').Watch.CatchUpState);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreSame(lost, thrown);
    }

    [TestMethod]
    public async Task EveryDriveFaulted_LeavesEachDrivesFaultForItsOwnStop()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var driveTFailure = new IOException("T's journal wrapped");
        var driveUFailure = new IOException("U's journal wrapped");

        harness.Source.WatchFor('T').FailDrive(driveTFailure);
        harness.Source.WatchFor('U').FailDrive(driveUFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'U');

        foreach (var driveLetter in new[] { 'T', 'U' })
        {
            Assert.IsNotNull(harness.DriveFor(driveLetter).Watch.FailureMessage);
            Assert.AreEqual(DriveState.Ready, harness.DriveFor(driveLetter).State);
        }

        var thrownForU = await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('U', Token));
        var thrownForT = await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreSame(driveUFailure, thrownForU.InnerException);
        Assert.AreSame(driveTFailure, thrownForT.InnerException);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount, $"drive {handle.DriveLetter}");
        }
    }

    [TestMethod]
    public async Task SubscriberFault_DoesNotDropTheDrive()
    {
        using var harness = new WatchHarness();
        var changeCount = 0;
        harness.Index.Changed += _ =>
        {
            changeCount++;
            if (changeCount == 1)
            {
                throw new InvalidOperationException("subscriber failed");
            }
        };

        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt", nextUsn: 200));
        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt", nextUsn: 300));

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, harness.Faults[0].Kind);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task CallerTokenCancellation_DuringStart_EndsTheStartWithNoFault()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        using var callerCancellation = new CancellationTokenSource();
        var start = harness.Index.StartWatchingAsync('T', callerCancellation.Token);
        await held.Entered.WaitAsync(HangGuard);

        await callerCancellation.CancelAsync();

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.AreEqual(0, harness.Faults.Count);
        Assert.AreEqual(0, harness.Source.Watches.Count);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').Watch.CatchUpState);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token),
            "the cancelled start cleared the request, so the drive is not watching");
    }

    [TestMethod]
    public async Task CallerTokenCancellation_AfterStart_LeavesTheWatchRunning()
    {
        using var harness = new WatchHarness();
        using var callerCancellation = new CancellationTokenSource();
        await harness.Index.StartWatchingAsync('T', callerCancellation.Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt", nextUsn: 200));

        await callerCancellation.CancelAsync();
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 10, "two.txt", nextUsn: 300));

        Assert.AreEqual(0, harness.Faults.Count);
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(0, harness.Source.Watches.Single().DisposeCount);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_WithACancelledToken_AbandonsTheWaitAndLeavesTheDriveRestartable()
    {
        using var harness = new WatchHarness();
        var applying = harness.TrackGate();
        harness.Index.Changed += _ =>
        {
            applying.MarkEntered();
            applying.WaitForRelease();
        };
        await harness.Index.StartWatchingAsync('T', Token);
        var firstHandle = harness.Source.WatchFor('T');
        _ = firstHandle.Queue(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await applying.Entered.WaitAsync(HangGuard);

        using var cancelledStop = new CancellationTokenSource();
        await cancelledStop.CancelAsync();
        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync('T', cancelledStop.Token));

        applying.Release();
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Assert.AreEqual(1, firstHandle.DisposeCount);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task StartAfterStop_LeavesNoOrphanedHandleAndClearsTheWatchFailure()
    {
        using var harness = new WatchHarness();
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        harness.Source.WatchFor('T').FailDrive(new IOException("journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.IsNotNull(harness.DriveFor('T').Watch.FailureMessage);
        await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(
            () => harness.Index.StopWatchingAsync('T', Token));

        await harness.Index.StartWatchingAsync('T', Token);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "after.txt", nextUsn: 300));
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);

        for (var iteration = 0; iteration < 20; iteration++)
        {
            await harness.Index.StartWatchingAsync('T', Token);
            await harness.Index.StopWatchingAsync('T', Token);
        }

        Assert.AreEqual(22, harness.Source.Watches.Count);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount, "every started handle was disposed exactly once");
        }
    }

}
