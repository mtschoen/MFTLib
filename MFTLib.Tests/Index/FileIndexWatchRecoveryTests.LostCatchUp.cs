using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A recovery rescan whose catch-up is lost (spec 2.6.6): the losses are scripted on the fake
///     producer's result, as in <see cref="FileIndexCatchUpLossTests" />, and no clock is involved.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    static WatchHarnessScan Held(TestGate? hold = null) => new(Hold: hold);

    [TestMethod]
    public async Task RecoveryRescan_LosesCatchUpTwiceThenSucceeds_ReachesCaughtUp_NoRecoveryFault()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Held());
        var producedBefore = harness.ProductionCount('T');

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var losses = CatchUpLosses(harness, 'T');
        Assert.AreSequenceEqual([1, 2], [.. harness.CatchUpLossStatuses('T').Select(status => status.Watch.ConsecutiveLostCatchUps)]);
        Assert.DoesNotContain(loss => loss.IsRecoveryStopped, losses);
        Assert.AreSequenceEqual(
            [WatchFaultKind.Volume, WatchFaultKind.CatchUpLost, WatchFaultKind.CatchUpLost], FaultKinds(harness, 'T'));
        var root = harness.RootFor('T');
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.CatchingUp, root.Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task RecoveryRescan_CatchUpLostThreeTimes_StopsWithRecoveryStopped_StaysFaulted_NoFurtherRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'), Held());
        var producedBefore = harness.ProductionCount('T');

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreSequenceEqual(
            [
                WatchFaultKind.Volume, WatchFaultKind.CatchUpLost, WatchFaultKind.CatchUpLost,
                WatchFaultKind.CatchUpLost
            ], FaultKinds(harness, 'T'));
        Assert.AreSequenceEqual([false, false, true], [.. CatchUpLosses(harness, 'T').Select(loss => loss.IsRecoveryStopped)]);
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.AreEqual(3, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(root.Watch.IsRecoveryStopped);
        Assert.AreSequenceEqual([false, false, true], [.. harness.CatchUpLossStatuses('T').Select(status => status.Watch.IsRecoveryStopped)]);
        Assert.HasCount(1, harness.Source.TargetsFor('T'));

        _ = harness.IndexFor('T').Status;
        Assert.IsFalse(harness.IndexFor('T').TryGetRecoveryCompletionForTest(new VolumeKey('T'), out _), "reading the roots queues no recovery");
        Assert.AreEqual(1, harness.RecoveryCount('T'));
        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore);
    }

    [TestMethod]
    public async Task StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var held = harness.TrackGate();
        harness.ScriptScans('T', Lost('T', hold: held), Held());
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await held.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        held.Release();
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the retry re-checks the watch request");
        Assert.IsEmpty(CatchUpLosses(harness, 'T'), "the stopped view held no watch request when the loss settled");
        Assert.HasCount(1, harness.Source.TargetsFor('T'));
        var root = harness.RootFor('T');
        Assert.AreEqual(WatchState.NotStarted, root.Watch.State);
        Assert.AreEqual(1, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(FaultKinds(harness, 'T').Contains(WatchFaultKind.Recovery));
    }

    [TestMethod]
    public async Task CatchUpCounts_ArePerRoot_OtherRootRecoversNormally()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        harness.Source.WatchFor('U').FailVolume(new IOException("U's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'U');
        await harness.WaitForRecoveryAsync('T');
        await harness.WaitForRecoveryAsync('U');

        var rootT = harness.RootFor('T');
        Assert.AreEqual(3, rootT.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.Faulted, rootT.Watch.State);
        Assert.IsTrue(CatchUpLosses(harness, 'T')[^1].IsRecoveryStopped);
        var rootU = harness.RootFor('U');
        Assert.AreEqual(0, rootU.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(WatchState.CatchingUp, rootU.Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('U'));
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'U'));
    }

    [TestMethod]
    public async Task ManualRescanAfterCatchUpStop_RestoresWatchAndResetsCount()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), Lost('T'), Lost('T'));
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        var root = harness.RootFor('T');
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(root.Watch.FailureMessage);
        Assert.IsFalse(root.Watch.IsRecoveryStopped);
        Assert.AreEqual(WatchState.CatchingUp, root.Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('T'));
    }

    /// <summary>
    ///     Spec 2.6.4: a wait issued while the root reads <see cref="WatchState.Recovering" />
    ///     faults at once with the root's fault, here the lost catch-up a consumer rescan is retrying.
    /// </summary>
    [TestMethod]
    public async Task WaitForCatchUp_WhileARescanRetriesALostCatchUp_FaultsAtOnceWithTheLoss()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var heldRetry = harness.TrackGate();
        harness.ScriptScans('T', Lost('T'), Held(heldRetry));
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await heldRetry.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);

        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
            () => harness.IndexFor('T').WaitForCatchUpAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreSame(CatchUpLosses(harness, 'T').Single(), failure);
        heldRetry.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
    }
}
