using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.WatchStateChanged" />: every change of a root's
///     <see cref="RootWatchStatus.State" /> is delivered once, with the root's next
///     <see cref="RootWatchStatus.StateVersion" />, before the <see cref="FileIndex.WatchFaulted" />
///     of the fault that caused it, with the state lock free. Each case drives the index through the
///     harness's scripted producer and handles and reads the order the handlers ran in; no clock is
///     involved. One case makes the live-watch checkpoint check throw through
///     <c>JournalIsolation.OverrideJournalWindow</c>, a process-wide seam, hence
///     <see cref="DoNotParallelizeAttribute" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class FileIndexWatchStateChangedTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    /// <summary>
    ///     The root's recorded history in one line per event: a state as
    ///     <c>State:Version</c>, with <c>:Kind</c> when a fault caused it, and a
    ///     <see cref="FileIndex.WatchFaulted" /> fault as <c>!Kind</c>.
    /// </summary>
    static string[] History(WatchStateRecorder recorder, char driveLetter) =>
    [
        .. recorder.EventsFor(driveLetter).Select(item => item switch
        {
            WatchStateChange { Fault: { } fault } state => $"{state.Watch.State}:{state.Watch.StateVersion}:{fault.Kind}",
            WatchStateChange state => $"{state.Watch.State}:{state.Watch.StateVersion}",
            _ => $"!{((WatchFault)item).Kind}"
        })
    ];

    static void AssertHistory(WatchStateRecorder recorder, char driveLetter, params string[] expected)
    {
        Assert.AreSequenceEqual(expected, History(recorder, driveLetter), $"history of {driveLetter}: {string.Join(", ", History(recorder, driveLetter))}");
        AssertEveryStateChangingFaultFollowsItsState(recorder, driveLetter);
    }

    /// <summary>
    ///     The contract every case checks: a reported fault that changes the root's state (all but
    ///     a subscriber fault and a lost catch-up that is still being retried) was preceded by the
    ///     state change it caused, carrying that same fault.
    /// </summary>
    static void AssertEveryStateChangingFaultFollowsItsState(WatchStateRecorder recorder, char driveLetter)
    {
        var events = recorder.EventsFor(driveLetter);
        for (var position = 0; position < events.Count; position++)
        {
            if (events[position] is not WatchFault fault || fault.Kind == WatchFaultKind.Subscriber ||
                (fault.Kind == WatchFaultKind.CatchUpLost && fault.IsRecovering))
            {
                continue;
            }

            Assert.Contains(state => Equals(state.Fault, fault), events.Take(position).OfType<WatchStateChange>(),
                $"{fault.Kind} of {driveLetter} was reported before any state change carrying it: " +
                string.Join(", ", History(recorder, driveLetter)));
        }
    }

    /// <summary>The last event's snapshot is the root's status snapshot: every field agrees.</summary>
    static void AssertStatusMatchesLastEvent(WatchHarness harness, WatchStateRecorder recorder, char driveLetter)
    {
        var last = recorder.StatesFor(driveLetter)[^1];
        var status = harness.RootFor(driveLetter);
        Assert.AreEqual(status.Watch, last.Watch);
    }

    [TestMethod]
    public async Task StartCatchUpStop_RaisesOneEventPerChangeWithTheNextVersion()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        Assert.AreEqual(0, harness.RootFor('T').Watch.StateVersion);

        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertHistory(recorder, 'T', "CatchingUp:1");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');

        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');

        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "NotStarted:3");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
    }

    /// <summary>
    ///     Item 1 of MFTLib#330: a consumer learns that an automatic recovery finished from the
    ///     events alone, awaiting the restarted watch's CaughtUp with no status polling.
    /// </summary>
    [TestMethod]
    public async Task RootFaultRecovery_RaisesFaultedAndRecoveringBeforeWatchFaulted_ThenCatchingUpAndCaughtUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        var failedHandle = harness.Source.WatchFor('T');

        failedHandle.FailVolume(new IOException("T's journal wrapped"));
        var restarted = await recorder.WaitForStateAsync('T', state => state.Watch.StateVersion > 4);
        Assert.AreEqual(WatchState.CatchingUp, restarted.Watch.State);

        // The restarted watch registers, and reports CatchingUp, before its source returns the
        // handle the recovery then publishes.
        await harness.WaitForRecoveryAsync('T');
        Assert.AreNotSame(failedHandle, harness.Source.WatchFor('T'));
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "Faulted:3:Volume", "Recovering:4:Volume", "!Volume",
            "CatchingUp:5", "CaughtUp:6");
        var states = recorder.StatesFor('T');
        var reported = recorder.EventsFor('T').OfType<WatchFault>().Single();
        Assert.AreEqual(reported, states[2].Fault);
        Assert.AreEqual(reported, states[3].Fault);
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedRecoveryScanOrRestart_RaisesFaultedWithTheRecoveryFaultBeforeWatchFaulted(bool failRestart)
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        var failure = new IOException(failRestart ? "the recovery restart failed" : "the recovery scan failed");
        if (failRestart)
        {
            harness.Source.FailNextStartFor('T', failure);
        }
        else
        {
            harness.FailNextProduction('T', failure);
        }

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        var reported = await recorder.WaitForFaultAsync(WatchFaultKind.Recovery, 'T');
        await harness.WaitForRecoveryAsync('T');

        if (failRestart)
        {
            // The abandoned restart's own failure is the Recovery fault the recovery reports.
            Assert.AreSame(failure, reported.Exception);
            AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Volume", "Recovering:3:Volume", "!Volume",
                "CatchingUp:4", "Faulted:5:Recovery", "!Recovery");
        }
        else
        {
            AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2:Volume", "Recovering:3:Volume", "!Volume",
                "Faulted:4:Recovery", "!Recovery");
        }

        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        if (failRestart)
        {
            await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        }
        else
        {
            await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        }

        AssertStatusMatchesLastEvent(harness, recorder, 'T');
    }

    /// <summary>
    ///     A recovered watch that ends before it first catches up queues no further recovery: a
    ///     volume fault is reported as a failed recovery, and a lost channel as itself.
    /// </summary>
    [TestMethod]
    [DataRow(false, "Faulted:6:Recovery", "!Recovery")]
    [DataRow(true, "Faulted:6:Channel", "!Channel")]
    public async Task RecoveredWatchEndingBeforeItCatchesUp_RaisesFaultedWithTheReportedFault(bool loseChannel,
        string faulted, string reported)
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        _ = await recorder.WaitForStateAsync('T', state => state.Watch.StateVersion == 5);
        await harness.WaitForRecoveryAsync('T');

        if (loseChannel)
        {
            harness.Source.WatchFor('T').LoseChannel(new IOException("the pipe broke"));
        }
        else
        {
            harness.Source.WatchFor('T').FailVolume(new IOException("wrapped again"));
        }

        await recorder.WaitForFaultAsync(loseChannel ? WatchFaultKind.Channel : WatchFaultKind.Recovery, 'T');

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "Faulted:3:Volume", "Recovering:4:Volume", "!Volume",
            "CatchingUp:5", faulted, reported);
    }

    [TestMethod]
    public async Task ManualRescan_RaisesCatchingUpWhenItRetiresTheWatchAndCaughtUpWhenTheReplacementCatchesUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3");
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "CaughtUp:4");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task RescanWhoseReplacementCannotStart_RaisesFaultedBeforeWatchFaulted()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        harness.Source.FailNextStartFor('T', new IOException("rearm failed"));

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "Faulted:4:RescanRestart",
            "!RescanRestart");
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "Faulted:4:RescanRestart",
            "!RescanRestart", "NotStarted:5");
    }

    [TestMethod]
    public async Task LostCatchUpRetried_RaisesRecoveringWithTheLossThenCatchingUp()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        harness.ScriptScans('T', new WatchHarnessScan(CatchUpLossTestSupport.StandardCatchUpLoss('T')), new WatchHarnessScan());

        await harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "CatchingUp:3", "Recovering:4:CatchUpLost",
            "!CatchUpLost", "CatchingUp:5", "CaughtUp:6");
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
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
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        string[] opening = ["CatchingUp:1"];
        if (initiallyCaughtUp)
        {
            await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
            opening = [.. opening, "CaughtUp:2"];
        }

        var after = opening.Length;
        string At(string state, int stepsAfterOpening) => $"{state}:{after + stepsAfterOpening}";
        harness.ScriptScans('T', new WatchHarnessScan(CatchUpLossTestSupport.StandardCatchUpLoss('T')),
            new WatchHarnessScan(CatchUpLossTestSupport.StandardCatchUpLoss('T')),
            new WatchHarnessScan(CatchUpLossTestSupport.StandardCatchUpLoss('T')));

        JournalCatchUpLostException stopped;
        if (automaticRecovery)
        {
            harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
            await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
            await harness.WaitForRecoveryAsync('T');
            AssertHistory(recorder, 'T', [
                .. opening, At("Faulted", 1) + ":Volume", At("Recovering", 2) + ":Volume", "!Volume",
                "!CatchUpLost", "!CatchUpLost", At("Faulted", 3) + ":CatchUpLost", "!CatchUpLost"
            ]);
            stopped = (JournalCatchUpLostException)recorder.StatesFor('T')[^1].Fault!.Exception;
        }
        else
        {
            stopped = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(
                () => harness.IndexFor('T').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));
            AssertHistory(recorder, 'T', [
                .. opening, At("CatchingUp", 1), At("Recovering", 2) + ":CatchUpLost", "!CatchUpLost",
                "!CatchUpLost", At("Faulted", 3) + ":CatchUpLost", "!CatchUpLost"
            ]);
        }

        Assert.IsTrue(stopped.IsRecoveryStopped);
        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreSame(stopped, recorder.StatesFor('T')[^1].Fault!.Exception);
        if (automaticRecovery)
        {
            await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('T').StopWatchingAsync(Token));
        }
        else
        {
            await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        }

        Assert.AreEqual(At("NotStarted", 4), History(recorder, 'T')[^1]);

        // A refusal of a root that is not faulted is a change of its own.
        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual(At("Faulted", 5), History(recorder, 'T')[^1]);
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task StartWhoseSourceThrows_RaisesFaultedWithNoFault()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        harness.Source.FailNextStartFor('T', new IOException("no channel"));

        await RootResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken));
        AssertHistory(recorder, 'T', "CatchingUp:1", "Faulted:2");
        var faulted = recorder.StatesFor('T')[^1];
        Assert.AreEqual("no channel", faulted.Watch.FailureMessage);
        AssertStatusMatchesLastEvent(harness, recorder, 'T');
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

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

        var start = harness.IndexFor('T').StartWatchingAsync(cancellation.Token);
        await starting.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(HangGuard, TestContext.CancellationToken));
        AssertHistory(recorder, 'T', "CatchingUp:1", "NotStarted:2");
    }

    [TestMethod]
    public async Task VersionsOfDifferentRoots_AreIndependent()
    {
        using var harness = new WatchHarness('T', 'U');
        var recorder = new WatchStateRecorder(harness.Indices);

        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        Assert.AreEqual(0, harness.RootFor('U').Watch.StateVersion);
        Assert.IsEmpty(recorder.StatesFor('U'));
        await harness.IndexFor('U').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2");
        AssertHistory(recorder, 'U', "CatchingUp:1");
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('U').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    /// <summary>
    ///     Item 11 of MFTLib#330: a consumer that decided a root was ready from a status it read,
    ///     tagged with that status's version, cannot publish that decision over a fault that landed
    ///     after the read, whatever order the two reach it in, and the events converge on the same
    ///     state when they arrive in any order.
    /// </summary>
    [TestMethod]
    public async Task ReadinessTaggedWithAVersion_IsSupersededByAFaultThatLandsAfterIt()
    {
        using var harness = new WatchHarness('T');
        var recorder = new WatchStateRecorder(harness.Index);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        var readiness = harness.RootFor('T');
        Assert.AreEqual(WatchState.CaughtUp, readiness.Watch.State);
        var recoveryScan = harness.HoldNextProduction('T');

        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');

        var inOrder = new VersionedRootState();
        foreach (var state in recorder.StatesFor('T'))
        {
            inOrder.Apply(state.Watch.State, state.Watch.StateVersion);
        }

        inOrder.Apply(readiness.Watch.State, readiness.Watch.StateVersion);
        Assert.AreEqual(WatchState.Recovering, inOrder.State, "the stale readiness lost to the later fault");

        var reversed = new VersionedRootState();
        reversed.Apply(readiness.Watch.State, readiness.Watch.StateVersion);
        foreach (var state in recorder.StatesFor('T').Reverse())
        {
            reversed.Apply(state.Watch.State, state.Watch.StateVersion);
        }

        Assert.AreEqual(inOrder.State, reversed.State);
        Assert.AreEqual(inOrder.Version, reversed.Version);
        recoveryScan.Release();
        await harness.WaitForRecoveryAsync('T');
        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
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
        var gates = new[] { harness.TrackGate(), harness.TrackGate() };
        var entered = Enumerable.Range(0, 2).Select(_ =>
            new TaskCompletionSource<(WatchStateChange Change, long VisibleVersion,
                Task RejectedStop)>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var exited = Enumerable.Range(0, 2).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        void Observe(WatchStateChange change)
        {
            var position = change.Watch.State switch
            {
                WatchState.CatchingUp => 0,
                WatchState.CaughtUp => 1,
                _ => -1
            };
            if (position < 0)
            {
                return;
            }

            var visible = index.Status;
            var rejected = index.StopWatchingAsync(CancellationToken.None);
            entered[position].TrySetResult((change, visible.Watch.StateVersion, rejected));
            try
            {
                gates[position].MarkEntered();
                gates[position].WaitForRelease();
            }
            finally
            {
                exited[position].TrySetResult();
            }
        }

        async Task VerifyDeliveryAsync(int position, Task operation)
        {
            Task<RootStatus>? reader = null;
            try
            {
                var observation = await entered[position].Task.WaitAsync(HangGuard, TestContext.CancellationToken);
                await gates[position].Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                reader = Task.Factory.StartNew(
                    () => index.Status,
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var read = await reader.WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.IsFalse(exited[position].Task.IsCompleted,
                    "the second-thread read completed while the subscriber was held");
                Assert.IsGreaterThanOrEqualTo(observation.Change.Watch.StateVersion, observation.VisibleVersion);
                Assert.IsGreaterThanOrEqualTo(observation.Change.Watch.StateVersion, read.Watch.StateVersion);
                var rejection = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => observation.RejectedStop.WaitAsync(HangGuard, TestContext.CancellationToken));
                Assert.Contains("WatchStateChanged", rejection.Message);
            }
            finally
            {
                gates[position].Release();
                if (reader is not null)
                {
                    await reader.WaitAsync(HangGuard, TestContext.CancellationToken);
                }
                await operation.WaitAsync(HangGuard, TestContext.CancellationToken);
            }
        }

        index.WatchStateChanged += Observe;
        try
        {
            var start = Task.Run(() => index.StartWatchingAsync(Token), Token);
            await VerifyDeliveryAsync(0, start);
            var catchUp = harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
            await VerifyDeliveryAsync(1, catchUp);
            Assert.AreEqual(WatchState.CaughtUp, harness.RootFor('T').Watch.State);
        }
        finally
        {
            foreach (var gate in gates)
            {
                gate.Release();
            }
            index.WatchStateChanged -= Observe;
        }
        await index.StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ThrowingHandler_DoesNotKeepChangesFromOtherHandlersOrStopTheWatch()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        index.WatchStateChanged += _ => throw new InvalidOperationException("a broken subscriber");
        var recorder = new WatchStateRecorder(index);

        await index.StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());
        await index.StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "NotStarted:3");
        Assert.IsEmpty(harness.Faults);
    }

    [TestMethod]
    public async Task Disposal_RaisesEachWatchedRootsLastChangeBeforeItReturns()
    {
        var harness = new WatchHarness('T', 'U');
        var recorder = new WatchStateRecorder(harness.Indices);
        await harness.IndexFor('T').StartWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.Source.WatchFor('T').Publish(new VolumeCaughtUp());

        harness.Dispose();

        AssertHistory(recorder, 'T', "CatchingUp:1", "CaughtUp:2", "NotStarted:3");
        AssertHistory(recorder, 'U');
    }

    /// <summary>A consumer's view of one root that applies only states newer than the one it holds.</summary>
    sealed class VersionedRootState
    {
        public WatchState State { get; private set; }

        public long Version { get; private set; }

        public void Apply(WatchState state, long version)
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
