using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.WatchStateChanged" />: every change of a drive's
///     <see cref="DriveStatus.WatchCatchUp" /> is delivered once, with the drive's next
///     <see cref="DriveStatus.WatchStateVersion" />, before the <see cref="FileIndex.WatchFaulted" />
///     of the fault that caused it, with the state lock free. Each case drives the index through the
///     harness's scripted producer and handles and reads the order the handlers ran in; no clock is
///     involved. One case makes the live-watch checkpoint check throw through
///     <c>JournalCheckpointCheck.OverrideJournalForTest</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexWatchStateChangedTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    /// <summary>
    ///     The drive's recorded history in one line per event: a state as
    ///     <c>State:Version</c>, with <c>:Kind</c> when a fault caused it, and a
    ///     <see cref="FileIndex.WatchFaulted" /> fault as <c>!Kind</c>.
    /// </summary>
    static string[] History(WatchStateRecorder recorder, char driveLetter) =>
    [
        .. recorder.EventsFor(driveLetter).Select(item => item switch
        {
            DriveWatchState { Fault: { } fault } state => $"{state.State}:{state.Version}:{fault.Kind}",
            DriveWatchState state => $"{state.State}:{state.Version}",
            _ => $"!{((WatchFault)item).Kind}"
        })
    ];

    static void AssertHistory(WatchStateRecorder recorder, char driveLetter, params string[] expected)
    {
        CollectionAssert.AreEqual(expected, History(recorder, driveLetter),
            $"history of {driveLetter}: {string.Join(", ", History(recorder, driveLetter))}");
        AssertEveryStateChangingFaultFollowsItsState(recorder, driveLetter);
    }

    /// <summary>
    ///     The contract every case checks: a reported fault that changes the drive's state (all but
    ///     a subscriber fault and a lost catch-up that is still being retried) was preceded by the
    ///     state change it caused, carrying that same fault.
    /// </summary>
    static void AssertEveryStateChangingFaultFollowsItsState(WatchStateRecorder recorder, char driveLetter)
    {
        var events = recorder.EventsFor(driveLetter);
        for (var position = 0; position < events.Count; position++)
        {
            if (events[position] is not WatchFault fault || fault.Kind == WatchFaultKind.Subscriber ||
                fault.Exception is JournalCatchUpLostException { RecoveryStopped: false })
            {
                continue;
            }

            Assert.IsTrue(events.Take(position).OfType<DriveWatchState>().Any(state => Equals(state.Fault, fault)),
                $"{fault.Kind} of {driveLetter} was reported before any state change carrying it: " +
                string.Join(", ", History(recorder, driveLetter)));
        }
    }

    /// <summary>The drive's status agrees with the last event: same state, same version.</summary>
    static void AssertStatusMatchesLastEvent(WatchHarness harness, WatchStateRecorder recorder, char driveLetter)
    {
        var last = recorder.StatesFor(driveLetter)[^1];
        var status = harness.DriveFor(driveLetter);
        Assert.AreEqual(last.State, status.WatchCatchUp);
        Assert.AreEqual(last.Version, status.WatchStateVersion);
    }

    [TestMethod]
    public async Task StartCatchUpStop_RaisesOneEventPerChangeWithTheNextVersion()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        Assert.AreEqual(0, harness.DriveFor('T').WatchStateVersion);

        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        AssertHistory(recorder, 'T', "CatchingUp:1");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');

        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "NotStarted:3");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
    }

    /// <summary>
    ///     Item 1 of MFTLib#330: a consumer learns that an automatic recovery finished from the
    ///     events alone, awaiting the restarted watch's CaughtUp with no status polling.
    /// </summary>
    [TestMethod]
    public async Task DriveFaultRecovery_RaisesFaultedAndRecoveringBeforeWatchFaulted_ThenCatchingUpAndCaughtUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        var failedHandle = harness.Source.WatchFor('T');

        failedHandle.FailDrive(new IOException("T's journal wrapped"));
        var restarted = await recorder.WaitForStateAsync('T', state => state.Version > 4);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, restarted.State);

        // The restarted watch registers, and reports CatchingUp, before its source returns the
        // handle the recovery then publishes.
        await harness.WaitForRecoveryAsync('T');
        Assert.AreNotSame(failedHandle, harness.Source.WatchFor('T'));
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "Faulted:3:Drive", "Recovering:4:Drive", "!Drive",
            "CatchingUp:5", "CaughtUp:6");
        var states = recorder.StatesFor('T');
        var reported = recorder.EventsFor('T').OfType<WatchFault>().Single();
        Assert.AreEqual(reported, states[2].Fault);
        Assert.AreEqual(reported, states[3].Fault);
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedRecoveryScanOrRestart_RaisesFaultedWithTheRecoveryFaultBeforeWatchFaulted(bool failRestart)
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var failure = new IOException(failRestart ? "the recovery restart failed" : "the recovery scan failed");
        if (failRestart)
        {
            harness.Source.FailNextStartFor('T', failure);
        }
        else
        {
            harness.FailNextProduction('T', failure);
        }

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        var reported = await recorder.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        if (failRestart)
        {
            // The abandoned restart's own failure is the Recovery fault the recovery reports.
            Assert.AreSame(failure, reported.Exception);
            AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Drive", "Recovering:3:Drive", "!Drive",
                "CatchingUp:4", "Faulted:5:Recovery", "!Recovery");
        }
        else
        {
            AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Drive", "Recovering:3:Drive", "!Drive",
                "Faulted:4:Recovery", "!Recovery");
        }

        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        if (failRestart)
        {
            await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
        }
        else
        {
            await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        }

        AssertStatusMatchesLastEvent(harness, recorder, 'T');
    }

    /// <summary>
    ///     A recovered watch that ends before it first catches up queues no further recovery: a
    ///     drive fault is reported as a failed recovery, and a lost channel as itself.
    /// </summary>
    [TestMethod]
    [DataRow(false, "Faulted:6:Recovery", "!Recovery")]
    [DataRow(true, "Faulted:6:Channel", "!Channel")]
    public async Task RecoveredWatchEndingBeforeItCatchesUp_RaisesFaultedWithTheReportedFault(bool loseChannel,
        string faulted, string reported)
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        _ = await recorder.WaitForStateAsync('T', state => state.Version == 5);
        await harness.WaitForRecoveryAsync('T');

        if (loseChannel)
        {
            harness.Source.WatchFor('T').LoseChannel(new IOException("the pipe broke"));
        }
        else
        {
            harness.Source.WatchFor('T').FailDrive(new IOException("wrapped again"));
        }

        await recorder.WaitForFaultAsync(loseChannel ? WatchFaultKind.Channel : WatchFaultKind.Recovery, 'T');

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "Faulted:3:Drive", "Recovering:4:Drive", "!Drive",
            "CatchingUp:5", faulted, reported);
    }

    [TestMethod]
    public async Task ManualRescan_RaisesCatchingUpWhenItRetiresTheWatchAndCaughtUpWhenTheReplacementCatchesUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3");
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "CaughtUp:4");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task RescanWhoseReplacementCannotStart_RaisesFaultedBeforeWatchFaulted()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        harness.Source.FailNextStartFor('T', new IOException("rearm failed"));

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "Faulted:4:RescanRestart",
            "!RescanRestart");
        await ThrowsAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync('T', Token));
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "Faulted:4:RescanRestart",
            "!RescanRestart", "NotStarted:5");
    }

    [TestMethod]
    public async Task LostCatchUpRetried_RaisesRecoveringWithTheLossThenCatchingUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        harness.ScriptScans('T', new ScriptedScan(WatchDeduplicationTestSupport.StandardCatchUpLoss('T')), new ScriptedScan());

        await harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "Recovering:4:CatchUpLost",
            "!CatchUpLost", "CatchingUp:5", "CaughtUp:6");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    /// <summary>
    ///     A manual rescan, or an automatic recovery from a watch that had caught up or was still
    ///     catching up, reaching the lost catch-up limit. A watch that never caught up has one
    ///     change fewer, so every later version is one lower.
    /// </summary>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task LostCatchUpsAtTheLimit_RaiseFaultedWithTheStoppingLoss_AndARefusedStartChangesNothing(
        bool automaticRecovery, bool initiallyCaughtUp)
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        string[] opening = ["CatchingUp:1"];
        if (initiallyCaughtUp)
        {
            await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
            opening = [.. opening, "CaughtUp:2"];
        }

        var after = opening.Length;
        string At(string state, int stepsAfterOpening) => $"{state}:{after + stepsAfterOpening}";
        harness.ScriptScans('T', new ScriptedScan(WatchDeduplicationTestSupport.StandardCatchUpLoss('T')),
            new ScriptedScan(WatchDeduplicationTestSupport.StandardCatchUpLoss('T')),
            new ScriptedScan(WatchDeduplicationTestSupport.StandardCatchUpLoss('T')));

        JournalCatchUpLostException stopped;
        if (automaticRecovery)
        {
            harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
            await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
            await harness.WaitForRecoveryAsync('T');
            AssertHistory(recorder, 'T', [
                .. opening, At("Faulted", 1) + ":Drive", At("Recovering", 2) + ":Drive", "!Drive",
                "!CatchUpLost", "!CatchUpLost", At("Faulted", 3) + ":CatchUpLost", "!CatchUpLost"
            ]);
            stopped = (JournalCatchUpLostException)recorder.StatesFor('T')[^1].Fault!.Exception;
        }
        else
        {
            stopped = await ThrowsAsync<JournalCatchUpLostException>(
                () => harness.Index.RescanAsync('T', Token).WaitAsync(HangGuard));
            AssertHistory(recorder, 'T', [
                .. opening, At("CatchingUp", 1), At("Recovering", 2) + ":CatchUpLost", "!CatchUpLost",
                "!CatchUpLost", At("Faulted", 3) + ":CatchUpLost", "!CatchUpLost"
            ]);
        }

        Assert.IsTrue(stopped.RecoveryStopped);
        await ThrowsAsync<InvalidOperationException>(
            () => harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard));

        Assert.AreSame(stopped, recorder.StatesFor('T')[^1].Fault!.Exception);
        if (automaticRecovery)
        {
            await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('T', Token));
        }
        else
        {
            await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
        }

        Assert.AreEqual(At("NotStarted", 4), History(recorder, 'T')[^1]);

        // A refusal of a drive that is not faulted is a change of its own.
        await ThrowsAsync<InvalidOperationException>(
            () => harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard));
        Assert.AreEqual(At("Faulted", 5), History(recorder, 'T')[^1]);
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task StartWhoseSourceThrows_RaisesFaultedWithNoFault()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        harness.Source.FailNextStartFor('T', new IOException("no channel"));

        await ThrowsAsync<IOException>(() => harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard));
        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2");
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);

        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2", "NotStarted:3");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
    }

    [TestMethod]
    public async Task StartItsCallerCancels_ReturnsToNotStarted()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        var starting = harness.TrackGate();
        harness.Source.HoldStartFor('T', starting);
        using var cancellation = new CancellationTokenSource();

        var start = harness.Index.StartWatchingAsync('T', cancellation.Token);
        await starting.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();

        await ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard));
        AssertHistory(recorder, 'T', "CatchingUp:1", "NotStarted:2");
    }

    [TestMethod]
    public async Task VersionsOfDifferentDrives_AreIndependent()
    {
        using var harness = new WatchHarness('T', 'U');
        var recorder = new WatchStateRecorder(harness.Index);

        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        Assert.AreEqual(0, harness.DriveFor('U').WatchStateVersion);
        Assert.AreEqual(0, recorder.StatesFor('U').Count);
        await harness.Index.StartWatchingAsync('U', Token).WaitAsync(HangGuard);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2");
        AssertHistory(recorder, 'U', "CatchingUp:1");
        await harness.Index.StopWatchingAsync(['T', 'U'], Token).WaitAsync(HangGuard);
    }

    /// <summary>
    ///     Item 11 of MFTLib#330: a consumer that decided a drive was ready from a status it read,
    ///     tagged with that status's version, cannot publish that decision over a fault that landed
    ///     after the read, whatever order the two reach it in, and the events converge on the same
    ///     state when they arrive in any order.
    /// </summary>
    [TestMethod]
    public async Task ReadinessTaggedWithAVersion_IsSupersededByAFaultThatLandsAfterIt()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        var readiness = harness.DriveFor('T');
        Assert.AreEqual(WatchCatchUpState.CaughtUp, readiness.WatchCatchUp);
        var recoveryScan = harness.HoldNextProduction('T');

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');

        var inOrder = new VersionedDriveState();
        foreach (var state in recorder.StatesFor('T'))
        {
            inOrder.Apply(state.State, state.Version);
        }

        inOrder.Apply(readiness.WatchCatchUp, readiness.WatchStateVersion);
        Assert.AreEqual(WatchCatchUpState.Recovering, inOrder.State, "the stale readiness lost to the later fault");

        var reversed = new VersionedDriveState();
        reversed.Apply(readiness.WatchCatchUp, readiness.WatchStateVersion);
        foreach (var state in recorder.StatesFor('T').Reverse())
        {
            reversed.Apply(state.State, state.Version);
        }

        Assert.AreEqual(inOrder.State, reversed.State);
        Assert.AreEqual(inOrder.Version, reversed.Version);
        recoveryScan.Release();
        await harness.WaitForRecoveryAsync('T');
        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    /// <summary>
    ///     A handler runs after its change is visible, with the state lock free, and a lifecycle
    ///     call from it is rejected, as from a WatchFaulted handler.
    /// </summary>
    [TestMethod]
    public async Task Handler_RunsWithoutTheStateLock_SeesItsChange_AndCannotCallLifecycleMethods()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        var observations = new List<(DriveWatchState Event, bool StateLockHeld, long VisibleVersion)>();
        Task? rejectedStop = null;
        index.WatchStateChanged += state =>
        {
            var visible = index.Drives.Single(drive => drive.DriveLetter == 'T');
            observations.Add((state, index.IsStateLockHeldForTest, visible.WatchStateVersion));
            rejectedStop ??= index.StopWatchingAsync('T', CancellationToken.None);
        };

        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        Assert.AreEqual(2, observations.Count);
        foreach (var (state, stateLockHeld, visibleVersion) in observations)
        {
            Assert.IsFalse(stateLockHeld, $"version {state.Version} was delivered under the state lock");
            Assert.IsTrue(visibleVersion >= state.Version, "a change is visible before it is delivered");
        }

        var rejection = await ThrowsAsync<InvalidOperationException>(() => rejectedStop!);
        StringAssert.Contains(rejection.Message, "WatchStateChanged");
        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').WatchCatchUp);
        await index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task ThrowingHandler_DoesNotKeepChangesFromOtherHandlersOrStopTheWatch()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        index.WatchStateChanged += _ => throw new InvalidOperationException("a broken subscriber");
        var recorder = new WatchStateRecorder(index);

        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await index.StopWatchingAsync('T', Token).WaitAsync(HangGuard);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "NotStarted:3");
        Assert.AreEqual(0, harness.Faults.Count);
    }

    [TestMethod]
    public async Task Disposal_RaisesEachWatchedDrivesLastChangeBeforeItReturns()
    {
        var harness = new WatchHarness('T', 'U');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.Index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());

        harness.Dispose();

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "NotStarted:3");
        AssertHistory(recorder, 'U');
    }

    /// <summary>A consumer's view of one drive that applies only states newer than the one it holds.</summary>
    sealed class VersionedDriveState
    {
        public WatchCatchUpState State { get; private set; }

        public long Version { get; private set; }

        public void Apply(WatchCatchUpState state, long version)
        {
            if (version <= Version)
            {
                return;
            }

            State = state;
            Version = version;
        }
    }
}
