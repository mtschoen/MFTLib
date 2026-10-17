using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanFaultHandoffTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    [DataRow("production", false, false, false)]
    [DataRow("drain", false, false, false)]
    [DataRow("restart decision", false, false, false)]
    [DataRow("registration", false, false, false)]
    [DataRow("registration", false, true, false)]
    [DataRow("starting", false, false, false)]
    [DataRow("starting", true, false, false)]
    [DataRow("none", false, true, true)]
    [DataRow("production", false, true, true)]
    public async Task StopDuringHandoff_ReportsSubscriberFaultOnceAsFailedResult(string stage, bool faultDuringProduction,
        bool faultAfterRetirement = false, bool delayedOverlappingStop = false)
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');
        var subscriberFailure = new IOException(faultAfterRetirement ? "delayed subscriber failed" : "subscriber failed before rescan");
        void FailSubscriber(FileChange _) => throw subscriberFailure;
        if (!faultAfterRetirement)
        {
            index.Changed += FailSubscriber;
            await handle.Publish(WatchHarness.Batch(9, "before.txt"));
            index.Changed -= FailSubscriber;
            Assert.AreSame(subscriberFailure, (await harness.WaitForFaultAsync(WatchFaultKind.Subscriber, 'T')).Exception);
        }

        var held = stage == "production" ? harness.HoldNextProduction('T') : harness.TrackGate();
        var production = faultDuringProduction ? harness.HoldNextProduction('T') : null;
        var delivery = faultAfterRetirement ? harness.TrackGate() : null;
        var faultAnnounced = delayedOverlappingStop ? harness.TrackGate() : null;
        Task<IReadOnlyList<DriveOperationResult>> rescan = Task.FromResult<IReadOnlyList<DriveOperationResult>>([]);
        if (delayedOverlappingStop)
        {
            index.Hooks.BeforeWatchChanged = _ => HoldSynchronously(delivery!);
            index.Changed += FailSubscriber;
            index.WatchFaulted += _ => HoldSynchronously(faultAnnounced!);
            _ = handle.Queue(WatchHarness.Batch(10, "during.txt"));
            await delivery!.Entered.WaitAsync(HangGuard);
            if (stage == "production")
            {
                harness.SetNextProducedCursor('T', 13, 9000);
                rescan = Task.Run(() => index.RescanAsync(['T'], Token), Token);
                await held.Entered.WaitAsync(HangGuard);
            }
        }
        else if (stage == "drain")
        {
            index.Hooks.BeforeWatchChanged = _ => HoldSynchronously(held);
            var delivering = handle.Queue(WatchHarness.Batch(10, "during.txt"));
            await held.Entered.WaitAsync(HangGuard);
            var original = index.Root('T').DriveBlock;
            harness.SetNextProducedCursor('T', 13, 9000);
            rescan = index.RescanAsync(['T'], Token);
            Assert.AreNotSame(original, index.Root('T').DriveBlock);
            Assert.IsFalse(delivering.IsCompleted, "the retired pump is held during its drain");
            Assert.AreEqual(0, handle.DisposeCount);
        }
        else
        {
            var published = faultAfterRetirement ? harness.TrackGate() : null;
            var original = index.Root('T').DriveBlock;
            if (faultAfterRetirement)
            {
                index.Hooks.BeforeWatchChanged = _ => HoldSynchronously(delivery!);
                index.Changed += FailSubscriber;
                _ = handle.Queue(WatchHarness.Batch(10, "during.txt"));
                await delivery!.Entered.WaitAsync(HangGuard);
                index.Hooks.PublishInsideWriteGate = _ => published!.MarkEntered();
            }

            harness.SetNextProducedCursor('T', 13, 9000);
            switch (stage)
            {
                case "restart decision":
                    index.Hooks.BeforeRestartDecision = _ => HoldSynchronously(held);
                    break;
                case "registration":
                    index.Hooks.RestartBeforeRegistration = _ =>
                    {
                        held.MarkEntered();
                        return held.WaitForReleaseAsync(Token);
                    };
                    break;
                case "starting":
                    harness.Source.HoldStartFor('T', held);
                    break;
            }

            rescan = Task.Run(() => index.RescanAsync(['T'], Token), Token);
            if (production is not null)
            {
                await production.Entered.WaitAsync(HangGuard);
                handle.LoseChannel(new IOException("channel failed during production"));
                await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
                production.Release();
            }

            if (faultAfterRetirement)
            {
                await published!.Entered.WaitAsync(HangGuard);
                while (ReferenceEquals(original, index.Root('T').DriveBlock))
                {
                    await Task.Yield();
                }

                delivery!.Release();
                Assert.AreSame(subscriberFailure, (await harness.WaitForFaultAsync(WatchFaultKind.Subscriber, 'T')).Exception);
            }

            await held.Entered.WaitAsync(HangGuard);
        }

        Task<IReadOnlyList<DriveOperationResult>> stopping;
        Task<IReadOnlyList<DriveOperationResult>> overlappingStop;
        if (delayedOverlappingStop)
        {
            stopping = index.StopWatchingAsync(['T'], Token);
            delivery!.Release();
            await faultAnnounced!.Entered.WaitAsync(HangGuard);
            overlappingStop = index.StopWatchingAsync(['T'], Token);
            faultAnnounced.Release();
            if (stage == "production")
            {
                held.Release();
            }

            var stoppingFault = await CaptureFailureAsync(stopping);
            var overlappingFault = await CaptureFailureAsync(overlappingStop);
            var failures = new[] { stoppingFault, overlappingFault }.Where(item => item is not null).ToArray();
            Assert.AreEqual(1, failures.Length, "delayed fault must be consumed exactly once across overlapping stops");
            Assert.AreSame(subscriberFailure, failures[0]);
        }
        else
        {
            stopping = index.StopWatchingAsync(['T'], Token);
            overlappingStop = stage == "drain" ? index.StopWatchingAsync(['T'], Token)
                : Task.FromResult<IReadOnlyList<DriveOperationResult>>([]);
            held.Release();
            var failure = await DriveResultAssertions.FailureAsync<IOException>(() => stopping);
            Assert.AreSame(subscriberFailure, failure);
            await overlappingStop.WaitAsync(HangGuard);
        }

        if (stage == "starting")
        {
            await DriveResultAssertions.FailureAsync<OperationCanceledException>(() => rescan);
        }
        else
        {
            await rescan.WaitAsync(HangGuard);
        }

        Assert.AreEqual(1, handle.DisposeCount);
        Assert.AreEqual(WatchState.NotStarted, index.Drives.Single().Watch.State);
        await DriveResultAssertions.NotApplicableAsync(() => index.StopWatchingAsync(['T'], Token));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementStart_SupersedesTheOldSubscriberFault(bool startFails)
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(['T'], Token);
        var subscriberFailure = new IOException("old subscriber fault");
        void FailSubscriber(FileChange _) => throw subscriberFailure;
        index.Changed += FailSubscriber;
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "before.txt"));
        index.Changed -= FailSubscriber;
        harness.SetNextProducedCursor('T', 13, 9000);
        var startFailure = new IOException("replacement start failed");
        if (startFails)
        {
            harness.Source.FailNextStartFor('T', startFailure);
        }

        await index.RescanAsync(['T'], Token);

        if (startFails)
        {
            var fault = harness.Faults.Single(item => item.Kind == WatchFaultKind.RescanRestart);
            Assert.AreSame(startFailure, fault.Exception.InnerException);
            var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.StopWatchingAsync(['T'], Token));
            Assert.AreSame(fault.Exception, failure);
        }
        else
        {
            await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
            await index.WaitForCatchUpAsync(['T'], Token);
            await index.StopWatchingAsync(['T'], Token);
        }
    }

    [TestMethod]
    public async Task Commit_ClearsRecoveryQueuedDuringProductionBeforeTheOldPumpDrains()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync(['T'], Token);
        var original = index.Root('T').DriveBlock;
        var handle = harness.Source.WatchFor('T');
        var production = harness.HoldNextProduction('T');
        var rescan = index.RescanAsync(['T'], Token);
        await production.Entered.WaitAsync(HangGuard);
        var faultHandler = harness.TrackGate();
        index.WatchFaulted += _ => HoldSynchronously(faultHandler);
        handle.FailDrive(new IOException("watch failed during production"));
        await faultHandler.Entered.WaitAsync(HangGuard);
        Assert.AreEqual(WatchState.Recovering, index.Drives.Single().Watch.State);
        Assert.IsTrue(index.TryGetRecoveryCompletionForTest('T', out var recovery));
        var publishing = harness.TrackGate();
        index.Hooks.PublishInsideWriteGate = _ => publishing.MarkEntered();

        production.Release();
        await publishing.Entered.WaitAsync(HangGuard);
        await index.WaitForDriveWriteGateForTest('T').WaitAsync(HangGuard);
        index.ReleaseDriveWriteGateForTest('T');

        Assert.AreNotSame(original, index.Root('T').DriveBlock);
        Assert.AreEqual(0, handle.DisposeCount, "the fault handler still holds the old pump");
        Assert.IsFalse(rescan.IsCompleted);
        Assert.AreEqual(WatchState.Faulted, index.Drives.Single().Watch.State,
            "a recovery for the replaced block cannot remain published during the drain");
        Assert.IsFalse(index.TryGetRecoveryCompletionForTest('T', out _));
        Assert.IsFalse(recovery!.IsCompleted, "the superseded ticket still has to finish its own teardown");
        faultHandler.Release();
        await rescan.WaitAsync(HangGuard);
        await recovery.WaitAsync(HangGuard);
        Assert.AreEqual(2, harness.ProductionCount('T'), "the stale recovery never scans the replacement");
        Assert.AreEqual(WatchState.CatchingUp, index.Drives.Single().Watch.State);
        await index.StopWatchingAsync(['T'], Token);
    }

    static void HoldSynchronously(TestGate gate)
    {
        gate.MarkEntered();
        gate.WaitForRelease();
    }

    static async Task<Exception?> CaptureFailureAsync(Task<IReadOnlyList<DriveOperationResult>> task)
    {
        var result = (await task.WaitAsync(HangGuard)).Single();
        if (result.Outcome == DriveOperationOutcome.Failed)
        {
            Assert.IsInstanceOfType<IOException>(result.Failure);
        }
        else
        {
            Assert.IsNull(result.Failure);
        }

        return result.Failure;
    }
}
