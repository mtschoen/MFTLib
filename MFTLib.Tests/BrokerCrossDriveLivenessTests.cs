using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Liveness across drives, end to end: a <see cref="FileIndex" /> over drives <c>T</c> and
///     <c>U</c> on one in-process broker, a fake clock on the host and another on the client, each
///     moved one heartbeat interval at a time by <see cref="CrossDriveScenario" />. What one drive's
///     pipe does or fails to do never reaches the other drive, and the wire's liveness frames are
///     what keep an idle session alive.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public sealed partial class BrokerCrossDriveLivenessTests
{
    const string StallMessage = "No frame from the broker for 30 seconds";
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    // The intervals a 30 second limit takes to reach, at 5 seconds each.
    static readonly int IntervalsToStallLimit = (int)(BrokerLiveness.StallLimit / BrokerLiveness.HeartbeatInterval);

    /// <summary>
    ///     Spec 9, row 1. T's host writes are stuck, so T's pipe carries nothing while U's carries a
    ///     backlog and heartbeats. U catches up and applies while T is silent, T faults with
    ///     <see cref="WatchFaultKind.Channel" /> only when the client's clock reaches the stall limit,
    ///     and U, its host run and the process are untouched.
    /// </summary>
    [TestMethod]
    public async Task OneDriveStallsWhileOthersFlow()
    {
        await using var scenario = await CrossDriveScenario.OpenAsync();
        var index = scenario.Index;
        var token = scenario.Token;
        var tipOfU = new UsnJournalCursor(WatchHarness.JournalId, ScriptedWatchBrokerHarness.DefaultTip.NextUsn + 100);
        scenario.Broker.SetTip('U', tipOfU);
        await index.StartWatchingAsync('T', token);
        await index.StartWatchingAsync('U', token);
        await index.WaitForCatchUpAsync('T', token);
        var runOfT = await scenario.Broker.Watch('T').RunAsync(1);
        var runOfU = await scenario.Broker.Watch('U').RunAsync(1);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, scenario.DriveOf('U').WatchCatchUpState, "U has a backlog to read");
        scenario.BeginCadence('T', 'U');
        scenario.FreezeHostWrites('T');

        // U delivers its backlog and catches up while T's pipe is stuck.
        var backlogApplied = ChangeSignal.WhenApplied(index, "backlog.txt");
        runOfU.Push(40, "backlog.txt", tipOfU.NextUsn);
        await backlogApplied;
        await index.WaitForCatchUpAsync('U', token).WaitAsync(HangGuard);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, scenario.DriveOf('U').WatchCatchUpState);

        // One tick less than the limit on the client's clock: T has heard nothing for 25 seconds.
        for (var interval = 0; interval < IntervalsToStallLimit - 1; interval++)
        {
            await scenario.AdvanceIntervalAsync('T');
        }

        Assert.AreEqual(0, scenario.FaultsOf('T').Count, "T is silent but inside the stall limit");
        Assert.AreEqual(WatchCatchUpState.CaughtUp, scenario.DriveOf('T').WatchCatchUpState);
        await scenario.AdvanceIntervalAsync('T');

        var fault = await scenario.FaultAsync(WatchFaultKind.Channel, 'T');
        var lost = (BrokerChannelLostException)fault.Exception;
        Assert.AreEqual('T', lost.DriveLetter);
        Assert.AreEqual(StallMessage, lost.Message);
        Assert.AreEqual(WatchCatchUpState.Faulted, scenario.DriveOf('T').WatchCatchUpState);
        Assert.IsNotNull(scenario.DriveOf('T').WatchFailureMessage);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Channel }, scenario.FaultsOf('T').Select(other => other.Kind).ToArray(),
            "the lost channel starts no recovery");
        Assert.AreEqual(1, scenario.ScansOf('T'), "no recovery scan");

        // U is untouched: no fault, its host run is not cancelled, and its channel keeps heartbeating
        // and applying well past the limit T was faulted at.
        scenario.StopMeasuring('T');
        for (var interval = 0; interval < 2 * IntervalsToStallLimit; interval++)
        {
            await scenario.AdvanceIntervalAsync();
        }

        var afterApplied = ChangeSignal.WhenApplied(index, "after.txt");
        runOfU.Push(41, "after.txt", tipOfU.NextUsn + 100);
        await afterApplied;
        Assert.AreEqual(0, scenario.FaultsOf('U').Count);
        Assert.AreEqual(WatchCatchUpState.CaughtUp, scenario.DriveOf('U').WatchCatchUpState);
        Assert.IsFalse(runOfU.Cancelled.IsCompleted, "U's host watch was never cancelled");
        Assert.AreEqual(1, scenario.Broker.Watch('U').StartedCount);
        Assert.IsFalse(scenario.Broker.Process.Ended.IsCompleted, "only T's channel was lost");
        await runOfT.Cancelled.WaitAsync(HangGuard);
    }

    /// <summary>
    ///     Spec 9, "host watchdog names a wedged loop". T's source loop is alive but stays in one
    ///     processing step: the host keeps heartbeating T's pipe inside the processing limit, then
    ///     writes <c>Stalled</c> naming the step, which reaches the index as a
    ///     <see cref="WatchFaultKind.Channel" /> fault carrying the host's message. No recovery scan
    ///     starts, and U goes on heartbeating and applying.
    /// </summary>
    [TestMethod]
    public async Task HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery()
    {
        await using var scenario = await CrossDriveScenario.OpenAsync();
        var index = scenario.Index;
        var token = scenario.Token;
        var wedged = new TestGate();
        try
        {
            scenario.Broker.Watch('T').WedgeNextRunInProcessing("wedged loop", wedged);
            await index.StartWatchingAsync('T', token);
            await index.StartWatchingAsync('U', token);
            await index.WaitForCatchUpAsync('T', token);
            await index.WaitForCatchUpAsync('U', token);
            await wedged.Entered.WaitAsync(HangGuard);
            var runOfU = await scenario.Broker.Watch('U').RunAsync(1);
            scenario.BeginCadence('T', 'U');
            var intervalsWithinLimit = (int)(BrokerLiveness.ProcessingLimit / BrokerLiveness.HeartbeatInterval);

            // The first visit skips the pipe that just wrote CaughtUp; each later one heartbeats it, so
            // T's pipe is alive and the client never stalls, while the host's processing clock runs.
            for (var interval = 0; interval < intervalsWithinLimit - 1; interval++)
            {
                await scenario.AdvanceIntervalAsync();
            }

            Assert.AreEqual(0, scenario.FaultsOf('T').Count, "inside the processing limit T only heartbeats");
            await scenario.AdvanceIntervalAsync('T');

            var fault = await scenario.FaultAsync(WatchFaultKind.Channel, 'T');
            var lost = (BrokerChannelLostException)fault.Exception;
            Assert.AreEqual('T', lost.DriveLetter);
            StringAssert.Contains(lost.Message, "wedged loop");
            StringAssert.Contains(lost.Message, "made no progress for 30 seconds");
            Assert.AreEqual(WatchCatchUpState.Faulted, scenario.DriveOf('T').WatchCatchUpState);
            CollectionAssert.AreEqual(new[] { WatchFaultKind.Channel },
                scenario.FaultsOf('T').Select(other => other.Kind).ToArray(), "no recovery starts");
            Assert.AreEqual(1, scenario.ScansOf('T'), "no recovery scan");

            // U is untouched.
            scenario.StopMeasuring('T');
            for (var interval = 0; interval < IntervalsToStallLimit; interval++)
            {
                await scenario.AdvanceIntervalAsync();
            }

            var applied = ChangeSignal.WhenApplied(index, "u-after.txt");
            runOfU.Push(40, "u-after.txt", ScriptedWatchBrokerHarness.DefaultTip.NextUsn + 100);
            await applied;
            Assert.AreEqual(0, scenario.FaultsOf('U').Count);
            Assert.IsFalse(runOfU.Cancelled.IsCompleted);
            Assert.IsFalse(scenario.Broker.Process.Ended.IsCompleted);
        }
        finally
        {
            wedged.Release();
        }
    }

    /// <summary>
    ///     Spec 9, "idle watch stays alive". Both drives' host sources yield nothing for 120 seconds on
    ///     both clocks, four times the stall limit: the sender's heartbeats keep every pipe alive, no
    ///     fault is raised, and a batch that finally arrives is applied.
    /// </summary>
    [TestMethod]
    public async Task IdleWatchOverBroker_StaysAliveUnderFakeClock()
    {
        await using var scenario = await CrossDriveScenario.OpenAsync();
        var index = scenario.Index;
        var token = scenario.Token;
        await index.StartWatchingAsync('T', token);
        await index.StartWatchingAsync('U', token);
        await index.WaitForCatchUpAsync('T', token);
        await index.WaitForCatchUpAsync('U', token);
        var runOfT = await scenario.Broker.Watch('T').RunAsync(1);
        scenario.BeginCadence('T', 'U');

        for (var interval = 0; interval < 4 * IntervalsToStallLimit; interval++)
        {
            await scenario.AdvanceIntervalAsync();
        }

        Assert.AreEqual(0, scenario.Faults.Count);
        Assert.IsFalse(scenario.Broker.Process.Ended.IsCompleted);
        Assert.IsTrue(index.Drives.All(drive => drive.WatchCatchUpState == WatchCatchUpState.CaughtUp));
        var applied = ChangeSignal.WhenApplied(index, "late.txt");
        runOfT.Push(40, "late.txt", ScriptedWatchBrokerHarness.DefaultTip.NextUsn + 100);
        await applied;
        Assert.IsFalse(runOfT.Cancelled.IsCompleted);
    }

    /// <summary>
    ///     W4-2. A session with no request in flight and no channel open, on a client clock moved
    ///     ten times the stall limit, keeps its control pipe alive on the host's heartbeats: the
    ///     process has not ended, and it still answers a request afterwards.
    /// </summary>
    [TestMethod]
    public async Task IdleSessionOverBroker_NoRequestsNoChannels_KeepsProcessAlivePastStallLimit()
    {
        await using var scenario = await CrossDriveScenario.OpenAsync();
        var ended = scenario.Broker.Process.Ended;
        scenario.BeginCadence();

        for (var interval = 0; interval < 10 * IntervalsToStallLimit; interval++)
        {
            await scenario.AdvanceIntervalAsync();
        }

        Assert.IsFalse(scenario.Broker.Process.Ended.IsCompleted);
        Assert.IsFalse(ended.IsCompleted, "Ended is never raised");
        var volume = await scenario.Broker.Process.QueryVolumeAsync('C', CancellationToken.None)
            .WaitAsync(HangGuard);
        Assert.AreEqual(1024L * 100, volume.MftValidDataLength);
    }

    /// <summary>
    ///     Spec 9, "Drive fault recovers", over the wire. The host's watch on T fails: the client reads
    ///     an <c>Error</c> frame, T raises <see cref="WatchFaultKind.Drive" />, the index rescans T
    ///     through a scan channel of its own and starts a new watch pipe from the new block's cursor,
    ///     while U's pipe is never reopened.
    /// </summary>
    [TestMethod]
    public async Task HostErrorOverBroker_RecoversByRescan()
    {
        await using var scenario = await CrossDriveScenario.OpenAsync();
        var index = scenario.Index;
        var token = scenario.Token;
        await index.StartWatchingAsync('T', token);
        await index.StartWatchingAsync('U', token);
        await index.WaitForCatchUpAsync('T', token);
        await index.WaitForCatchUpAsync('U', token);
        var firstRunOfT = await scenario.Broker.Watch('T').RunAsync(1);
        var runOfU = await scenario.Broker.Watch('U').RunAsync(1);

        firstRunOfT.Fail(new IOException("T's journal wrapped"));

        var fault = await scenario.FaultAsync(WatchFaultKind.Drive, 'T');
        Assert.AreEqual("T's journal wrapped", fault.Exception.Message);
        var secondRunOfT = await scenario.Broker.Watch('T').RunAsync(2);
        Assert.AreEqual(ScriptedWatchBrokerHarness.DefaultTip, secondRunOfT.Since,
            "the recovered watch starts from the new block's cursor");
        Assert.AreEqual(2, scenario.ScansOf('T'), "the open's scan and the recovery's one scan");
        var applied = ChangeSignal.WhenApplied(index, "recovered.txt");
        secondRunOfT.Push(41, "recovered.txt", ScriptedWatchBrokerHarness.DefaultTip.NextUsn + 100);
        await applied;

        Assert.AreEqual(WatchCatchUpState.CaughtUp, scenario.DriveOf('T').WatchCatchUpState);
        Assert.IsNull(scenario.DriveOf('T').WatchFailureMessage);
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive },
            scenario.FaultsOf('T').Select(other => other.Kind).ToArray(), "recovered: no Recovery fault");
        Assert.AreEqual(1, index.Search(new SearchQuery("scan-T-2.txt", NameMatchMode.Exact), token).Count, "T's block is the recovery's");
        Assert.AreEqual(0, index.Search(new SearchQuery("scan-T-1.txt", NameMatchMode.Exact), token).Count);
        Assert.AreEqual(1, scenario.ScansOf('U'), "U was not rescanned");
        Assert.AreEqual(1, scenario.Broker.Watch('U').StartedCount, "U's pipe was not reopened");
        Assert.IsFalse(runOfU.Cancelled.IsCompleted);
        Assert.AreEqual(0, scenario.FaultsOf('U').Count);
    }
}
