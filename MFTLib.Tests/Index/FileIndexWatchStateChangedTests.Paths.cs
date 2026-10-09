using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     The remaining paths that change a drive's watch state, and the two delivery guarantees a
///     concurrent or failing settlement could break: a fault's state change is delivered before
///     its WatchFaulted even when another thread took it from the queue, and it is delivered even
///     when the checkpoint check after the settlement throws.
/// </summary>
public partial class FileIndexWatchStateChangedTests
{
    /// <summary>Polls a condition another thread makes true, bounded by the hang guard.</summary>
    static async Task UntilAsync(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        while (!condition())
        {
            await Task.Delay(1, guard.Token);
        }
    }

    /// <summary>
    ///     A stop lands between the pump settling a drive fault and the pump reporting it, takes the
    ///     queued Faulted with its own NotStarted, and is held inside the first handler. The pump
    ///     must wait for that delivery to finish before it raises WatchFaulted.
    /// </summary>
    [TestMethod]
    public async Task FaultStateTakenByAConcurrentStop_IsDeliveredBeforeTheFaultIsReported()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        var stopDelivering = harness.TrackGate();
        index.WatchStateChanged += state =>
        {
            if (state.CatchUpState == WatchCatchUpState.Faulted)
            {
                stopDelivering.MarkEntered();
                stopDelivering.WaitForRelease();
            }
        };
        var recorder = new WatchStateRecorder(index);
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Task? stop = null;
        index.PumpFaultSettlementWrapperForTest = settle =>
        {
            settle();
            stop = Task.Run(() => index.StopWatchingAsync('T', CancellationToken.None));
            stopDelivering.Entered.Wait(HangGuard);
        };
        var contendedBefore = index.ContendedWatchStateDeliveriesForTest;

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await stopDelivering.Entered.WaitAsync(HangGuard);
        await UntilAsync(() => index.ContendedWatchStateDeliveriesForTest > contendedBefore);
        stopDelivering.Release();
        await recorder.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "Faulted:3:Drive", "NotStarted:4", "!Drive");
        await ThrowsAsync<DriveWatchFaultException>(() => stop!.WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task CheckpointCheckThrowingAfterAFault_StillDeliversTheFaultedState()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(
            _ => throw new IOException("the journal query failed"));

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        var faulted = await recorder.WaitForStateAsync('T', state => state.CatchUpState == WatchCatchUpState.Faulted);

        Assert.AreEqual(2, faulted.StateVersion);
        Assert.AreEqual(WatchFaultKind.Drive, faulted.Fault?.Kind);
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task ConsumerStartSupersedingAQueuedRecovery_RaisesFaultedThenCatchingUp()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        var recorder = new WatchStateRecorder(index);
        var recoveryHeld = harness.TrackGate();
        index.RecoveryBeforeLifecycleGateForTest = async (_, token) =>
        {
            recoveryHeld.MarkEntered();
            await recoveryHeld.WaitForReleaseAsync(token);
        };
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await recorder.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        await recoveryHeld.Entered.WaitAsync(HangGuard);

        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        recoveryHeld.Release();
        await harness.WaitForRecoveryAsync('T');

        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Drive", "Recovering:3:Drive", "!Drive", "Faulted:4",
            "CatchingUp:5");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task StopBetweenARescansRetireAndRestart_RaisesNotStarted()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        var recorder = new WatchStateRecorder(index);
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Task? stop = null;
        index.BeforeRestartDecisionForTest = _ => stop ??= index.StopWatchingAsync('T', CancellationToken.None);

        await index.RescanAsync('T', Token).WaitAsync(HangGuard);
        await stop!.WaitAsync(HangGuard);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "NotStarted:4");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
    }

    [TestMethod]
    public async Task ChannelLost_RaisesFaultedWithTheChannelFaultAndNoRecovery()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);

        harness.Source.WatchFor('T').LoseChannel(new IOException("the pipe broke"));
        await recorder.WaitForFaultAsync(WatchFaultKind.Channel, 'T');

        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Channel", "!Channel");
        await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('T', Token));
        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Channel", "!Channel", "NotStarted:3");
    }

    [TestMethod]
    public async Task ApplyFault_RaisesFaultedAndRecoveringWithTheApplyFault()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);

        await harness.Source.WatchFor('T').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, 5000));
        await recorder.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.WaitForRecoveryAsync('T');

        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Apply", "Recovering:3:Apply", "!Apply",
            "CatchingUp:4");
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }
}
