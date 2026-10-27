using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The per-root fault lane. Every fault here is injected through a root's scripted handle or a
///     throwing subscriber, so none of these needs a broker, a volume, or Windows.
/// </summary>
[TestClass]
public class FileIndexWatchFaultTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task ApplyFailure_IsReportedAsAnApplyFaultAndIsolatesTheRoot()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var frozenCursor = harness.BlockFor('T').Header.UsnNextUsn;

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, NextUsn: 5000));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));
        await harness.WaitForRecoveryAsync('T');

        Assert.HasCount(1, harness.Faults);
        Assert.IsInstanceOfType<ArgumentNullException>(fault.Exception);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage, "the recovery replaced the faulted watch");
        Assert.AreEqual(RootState.Ready, harness.RootFor('T').State);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.HasCount(1, harness.Source.TargetsFor('U'));
        Assert.IsNull(harness.RootFor('U').Watch.FailureMessage);
        Assert.AreEqual(frozenCursor, harness.BlockFor('T').Header.UsnNextUsn, "the rejected batch advanced nothing");
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.HasCount(1, harness.Changes);
        Assert.AreEqual("u.txt", harness.Changes.Single().Entry.Name);

        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task DriveFailure_IsReportedAsADriveFaultCarryingTheDriveLetter()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var wrapped = new IOException("journal wrapped");
        var frozenCursor = harness.BlockFor('T').Header.UsnNextUsn;

        harness.Source.WatchFor('T').FailVolume(wrapped);
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));

        Assert.HasCount(1, harness.Faults);
        Assert.AreEqual('T', fault.Volume);
        var rootFault = (VolumeWatchFaultException)fault.Exception;
        Assert.AreSame(wrapped, rootFault.InnerException);
        Assert.AreEqual("journal wrapped", harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(RootState.Ready, harness.RootFor('T').State);
        Assert.AreEqual(frozenCursor, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);

        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreSame(rootFault, failure);
    }

    [TestMethod]
    public async Task ChannelLoss_EndsOnlyThatRootsPump_AndNamesTheRoot()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var lost = new IOException("the broker died");

        harness.Source.WatchFor('T').LoseChannel(lost);
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));

        Assert.HasCount(1, harness.Faults);
        Assert.AreEqual('T', fault.Volume);
        Assert.AreSame(lost, fault.Exception);
        Assert.AreEqual("the broker died", harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
        Assert.IsNull(harness.RootFor('U').Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('U').Watch.State);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);

        var failure = await RootResultAssertions.FailureAsync<IOException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreSame(lost, failure);
    }

    [TestMethod]
    public async Task EveryRootFaulted_LeavesEachRootsFaultForItsOwnStop()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var rootTFailure = new IOException("T's journal wrapped");
        var rootUFailure = new IOException("U's journal wrapped");

        harness.Source.WatchFor('T').FailVolume(rootTFailure);
        harness.Source.WatchFor('U').FailVolume(rootUFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'U');

        foreach (var driveLetter in new[] { 'T', 'U' })
        {
            Assert.IsNotNull(harness.RootFor(driveLetter).Watch.FailureMessage);
            Assert.AreEqual(RootState.Ready, harness.RootFor(driveLetter).State);
        }

        var thrownForU = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('U').StopWatchingAsync(Token));
        var thrownForT = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreSame(rootUFailure, thrownForU.InnerException);
        Assert.AreSame(rootTFailure, thrownForT.InnerException);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount, $"root {handle.Volume}");
        }
    }

    [TestMethod]
    public async Task SubscriberFault_DoesNotDropTheRoot()
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

        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt", nextUsn: 200));
        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt", nextUsn: 300));

        Assert.HasCount(1, harness.Faults);
        Assert.AreEqual(WatchFaultKind.Subscriber, harness.Faults[0].Kind);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);

        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task CallerTokenCancellation_DuringStart_EndsTheStartWithNoFault()
    {
        using var harness = new WatchHarness();
        var held = harness.TrackGate();
        harness.Source.HoldStart(held);
        using var callerCancellation = new CancellationTokenSource();
        var start = harness.IndexFor('T').StartWatchingAsync(callerCancellation.Token);
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await callerCancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.IsEmpty(harness.Faults);
        Assert.IsEmpty(harness.Source.Watches);
        Assert.AreEqual(WatchState.NotStarted, harness.RootFor('T').Watch.State);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        await RootResultAssertions.NotApplicableAsync(
            () => harness.IndexFor('T').StopWatchingAsync(Token),
            "the cancelled start cleared the request, so the root is not watching");
    }

    [TestMethod]
    public async Task CallerTokenCancellation_AfterStart_LeavesTheWatchRunning()
    {
        using var harness = new WatchHarness();
        using var callerCancellation = new CancellationTokenSource();
        await harness.IndexFor('T').StartWatchingAsync(callerCancellation.Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt", nextUsn: 200));

        await callerCancellation.CancelAsync();
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 10, "two.txt", nextUsn: 300));

        Assert.IsEmpty(harness.Faults);
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(0, harness.Source.Watches.Single().DisposeCount);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_WithACancelledToken_AbandonsTheWaitAndLeavesTheRootRestartable()
    {
        using var harness = new WatchHarness();
        var applying = harness.TrackGate();
        harness.Index.Changed += _ =>
        {
            applying.MarkEntered();
            applying.WaitForRelease();
        };
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var firstHandle = harness.Source.WatchFor('T');
        _ = firstHandle.Queue(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        using var cancelledStop = new CancellationTokenSource();
        await cancelledStop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.IndexFor('T').StopWatchingAsync(cancelledStop.Token));

        applying.Release();
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(1, firstHandle.DisposeCount);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartAfterStop_LeavesNoOrphanedHandleAndClearsTheWatchFailure()
    {
        using var harness = new WatchHarness();
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailVolume(new IOException("journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        Assert.IsNotNull(harness.RootFor('T').Watch.FailureMessage);
        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));

        await harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "after.txt", nextUsn: 300));
        Assert.AreEqual(300L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.IndexFor('T').StopWatchingAsync(Token);

        for (var iteration = 0; iteration < 20; iteration++)
        {
            await harness.IndexFor('T').StartWatchingAsync(Token);
            await harness.IndexFor('T').StopWatchingAsync(Token);
        }

        Assert.HasCount(22, harness.Source.Watches);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount, "every started handle was disposed exactly once");
        }
    }

}
