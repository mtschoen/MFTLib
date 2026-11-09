using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Automatic recovery while the volume is absent: the rescan is retried after each pause in
///     the internal schedule, on the index's injected clock, so no test waits on real time.
/// </summary>
public partial class FileIndexWatchRecoveryTests
{
    static async Task FaultVolumeWhileAbsentAsync(WatchHarness harness, CancellationToken token)
    {
        await harness.IndexFor('T').StartWatchingAsync(token);
        harness.SetVolumeAbsent('T', absent: true);
        harness.Source.WatchFor('T').FailVolume(new IOException("T was detached"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
    }

    [TestMethod]
    public async Task VolumeAbsentThenReturns_RecoveryRetriesAndReachesCaughtUp()
    {
        using var harness = new WatchHarness('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);

        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State, "the root stays Recovering through the pause");
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));

        harness.SetVolumeAbsent('T', absent: false);
        harness.Clock.Advance(FileIndex.VolumeScan.RecoveryRetryDelays[0]);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.HasCount(2, harness.Source.TargetsFor('T'), "the watch restarts from the retried block");
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'), "no Recovery fault is raised");
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task VolumeNeverReturns_RetriesOnTheScheduleThenRaisesRecovery()
    {
        using var harness = new WatchHarness('T');
        var producedBefore = harness.ProductionCount('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);

        var delays = FileIndex.VolumeScan.RecoveryRetryDelays;
        for (var retry = 0; retry < delays.Length; retry++)
        {
            await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
            Assert.AreEqual(retry + 1, harness.ProductionCount('T') - producedBefore, "one scan per pause");
            Assert.AreEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);
            harness.Clock.Advance(delays[retry]);
        }

        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.IsInstanceOfType<InvalidOperationException>(recoveryFault.Exception);
        Assert.AreEqual(delays.Length + 1, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(delays.Length, harness.Clock.PausesBegun);
        Assert.AreSequenceEqual([WatchFaultKind.Volume, WatchFaultKind.Recovery], FaultKinds(harness, 'T'));
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task ScanFailingTwiceWithVolumePresent_RaisesRecoveryWithoutPausing()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        harness.ScriptScans('T', new WatchHarnessScan(Failure: new IOException("the block would not parse")),
            new WatchHarnessScan(Failure: new IOException("the block still would not parse")));

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore, "one immediate re-attempt, then the failure stands");
        Assert.AreEqual(0, harness.Clock.PausesBegun, "no pause begins while the volume reads ready");
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task VolumeBackBeforeTheProbe_ReattemptsAtOnceAndReachesCaughtUp()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        harness.SetVolumeAbsent('T', absent: true);
        harness.ReturnVolumeBeforeNextProbe('T');

        harness.Source.WatchFor('T').FailVolume(new IOException("T was detached"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(2, harness.ProductionCount('T') - producedBefore);
        Assert.AreEqual(0, harness.Clock.PausesBegun, "no clock advance was needed");
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'), "no Recovery fault");
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task FlappingVolume_ReattemptsAtOnceOnlyOncePerRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var producedBefore = harness.ProductionCount('T');
        harness.ScriptScans('T', [.. Enumerable.Range(0, 20).Select(attempt =>
            new WatchHarnessScan(Failure: new IOException($"T flapped away during scan {attempt}")))]);
        harness.SetVolumeAbsent('T', absent: true);
        harness.FlapVolumeOnEveryProbe('T');

        harness.Source.WatchFor('T').FailVolume(new IOException("T was detached"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.Clock.Advance(FileIndex.VolumeScan.RecoveryRetryDelays[0]);
        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(3, harness.ProductionCount('T') - producedBefore,
            "scan, immediate re-attempt on ready, pause on absent, then the second ready failure stands");
        Assert.AreEqual(1, harness.Clock.PausesBegun);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task StopDuringRetryPause_EndsRecoveryWithoutFurtherScansOrRecoveryFault()
    {
        using var harness = new WatchHarness('T');
        var producedBefore = harness.ProductionCount('T');
        await FaultVolumeWhileAbsentAsync(harness, Token);
        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);

        var stopped = await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, Token);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(RootOperationOutcome.Failed, stopped.Outcome, "stop returns the watch's fault once");
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the abandoned retries never scan");
        Assert.AreSequenceEqual([WatchFaultKind.Volume], FaultKinds(harness, 'T'));
        Assert.AreNotEqual(WatchState.Recovering, harness.RootFor('T').Watch.State);
    }

    [TestMethod]
    public async Task VolumeAbsentAfterOwnCatchUpLossPublication_StillRetriesAndRecovers()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), new WatchHarnessScan(Failure: new IOException("T was detached")));
        harness.SetVolumeAbsent('T', absent: true);
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));

        await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
        harness.SetVolumeAbsent('T', absent: false);
        harness.Clock.Advance(FileIndex.VolumeScan.RecoveryRetryDelays[0]);
        await harness.WaitForRecoveryAsync('T');

        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.IsFalse(harness.Faults.Any(fault => fault.Kind == WatchFaultKind.Recovery));
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task VolumeNeverReturnsAfterOwnCatchUpLossPublication_RaisesRecovery()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.ScriptScans('T', Lost('T'), new WatchHarnessScan(Failure: new IOException("T was detached")));
        harness.SetVolumeAbsent('T', absent: true);
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));

        foreach (var delay in FileIndex.VolumeScan.RecoveryRetryDelays)
        {
            await harness.Clock.WaitForPauseAsync(Token).WaitAsync(HangGuard, Token);
            harness.Clock.Advance(delay);
        }

        await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('T').Watch.State);
    }
}
