using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSharedWatchTests
{
    [TestMethod]
    public async Task VolumeFault_ReachesEveryRequestingView_AndOneRecoveryRuns()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await using var work = await fixture.OpenAsync(RootKey.FromDirectory(fixture.WorkPath));
        var volumeFaults = new List<WatchFault>();
        var sourceFaults = new List<WatchFault>();
        var workFaults = new List<WatchFault>();
        volume.WatchFaulted += volumeFaults.Add;
        source.WatchFaulted += sourceFaults.Add;
        work.WatchFaulted += workFaults.Add;
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);

        watches.Watches.Single().FailVolume(new IOException("synthetic volume failure"));
        await watches.WaitForStartAsync(fixture.Volume, 2, CancellationToken.None).WaitAsync(HangGuard);
        await volume.WaitForCatchUpAsync(CancellationToken.None);

        Assert.AreEqual(2, fixture.ProductionCount);
        Assert.HasCount(1, volumeFaults);
        Assert.HasCount(1, sourceFaults);
        Assert.AreEqual(WatchFaultKind.Volume, sourceFaults[0].Kind);
        Assert.IsEmpty(workFaults);
    }

    [TestMethod]
    public async Task RescanRestartFault_ReachesEveryRequestingView()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        var first = new List<WatchFault>();
        var second = new List<WatchFault>();
        volume.WatchFaulted += first.Add;
        source.WatchFaulted += second.Add;
        watches.StartFailure = _ => new IOException("replacement start refused");

        Assert.AreEqual(RootOperationOutcome.Succeeded, (await volume.RescanAsync(CancellationToken.None)).Outcome);

        Assert.HasCount(1, first);
        Assert.HasCount(1, second);
        Assert.AreEqual(WatchFaultKind.RescanRestart, first[0].Kind);
        Assert.AreSame(first[0].Exception, second[0].Exception);
    }

    [TestMethod]
    public async Task Stop_OfTheLastRequesterDuringARescan_PreventsTheRestart()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        var gate = fixture.HoldProduction();
        var rescan = volume.RescanAsync(CancellationToken.None);
        try
        {
            await gate.Entered.WaitAsync(HangGuard);
            await volume.StopWatchingAsync(CancellationToken.None);
            await source.StopWatchingAsync(CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
        await rescan;
        Assert.HasCount(1, watches.Starts);
        Assert.AreEqual(WatchState.NotStarted, source.Status.Watch.State);
    }

    [TestMethod]
    public async Task WatchStateChanged_ReachesEveryAttachedView_InVersionOrder()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var first = new List<long>();
        var second = new List<long>();
        volume.WatchStateChanged += change => first.Add(change.Watch.StateVersion);
        source.WatchStateChanged += change => second.Add(change.Watch.StateVersion);

        await volume.StartWatchingAsync(CancellationToken.None);
        await source.WaitForCatchUpAsync(CancellationToken.None);
        await volume.StopWatchingAsync(CancellationToken.None);

        Assert.AreSequenceEqual(new long[] { 1, 2, 3 }, first);
        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    [DataRow("ready")]
    [DataRow("stop")]
    [DataRow("cancel")]
    [DataRow("dispose")]
    public async Task SecondStart_WhileReady_ReturnsWithoutASecondWatch(string mode)
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        if (mode == "ready")
        {
            await volume.StartWatchingAsync(CancellationToken.None);
            await source.StartWatchingAsync(CancellationToken.None);

            Assert.HasCount(1, watches.Starts);
            Assert.IsTrue(volume.Status.Watch.IsRequested);
            Assert.IsTrue(source.Status.Watch.IsRequested);
            return;
        }

        var gate = new TestGate();
        watches.HoldStart(gate, observeToken: false);
        using var cancellation = new CancellationTokenSource();
        var notStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.WatchStateChanged += change =>
        {
            if (change.Watch.State == WatchState.NotStarted)
            {
                notStarted.TrySetResult();
            }
        };
        var first = volume.StartWatchingAsync(cancellation.Token);
        Task withdrawal;
        Task<RootOperationResult> second;
        try
        {
            await gate.Entered.WaitAsync(HangGuard);
            if (mode == "stop")
            {
                withdrawal = volume.StopWatchingAsync(CancellationToken.None);
            }
            else if (mode == "cancel")
            {
                await cancellation.CancelAsync();
                withdrawal = Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first);
            }
            else
            {
                withdrawal = volume.DisposeAsync().AsTask();
            }

            await notStarted.Task.WaitAsync(HangGuard);
            second = source.StartWatchingAsync(CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }

        await withdrawal.WaitAsync(HangGuard);
        if (mode != "cancel")
        {
            Assert.AreEqual(RootOperationOutcome.Failed, (await first.WaitAsync(HangGuard)).Outcome);
        }

        Assert.AreEqual(RootOperationOutcome.Succeeded, (await second.WaitAsync(HangGuard)).Outcome);
        Assert.HasCount(2, watches.Starts);
        Assert.HasCount(1, watches.Watches.Where(watch => watch.DisposeCount == 0));
        Assert.IsTrue(source.Status.Watch.IsRequested);
    }

    [TestMethod]
    [DataRow("alone")]
    [DataRow("sibling-after-stop")]
    [DataRow("sibling-before-stop")]
    public async Task SecondStart_AfterRestartingTheSameView_SurvivesTheOlderCallsCancellation(string mode)
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        var gate = new TestGate();
        watches.HoldStart(gate, observeToken: false);
        using var cancellation = new CancellationTokenSource();
        var first = volume.StartWatchingAsync(cancellation.Token);
        Task<RootOperationResult>? sibling = null;
        Task<RootOperationResult> stop;
        Task<RootOperationResult> second;
        try
        {
            await gate.Entered.WaitAsync(HangGuard);
            if (mode == "sibling-before-stop")
            {
                sibling = source.StartWatchingAsync(CancellationToken.None);
            }

            stop = volume.StopWatchingAsync(CancellationToken.None);
            if (mode == "sibling-after-stop")
            {
                sibling = source.StartWatchingAsync(CancellationToken.None);
            }

            second = volume.StartWatchingAsync(CancellationToken.None);
            await cancellation.CancelAsync();
        }
        finally
        {
            gate.Release();
        }

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first.WaitAsync(HangGuard));
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await stop.WaitAsync(HangGuard)).Outcome);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await second.WaitAsync(HangGuard)).Outcome);
        Assert.IsTrue(volume.Status.Watch.IsRequested, "the replacement request survives the older call's cleanup");
        if (sibling is not null)
        {
            Assert.AreEqual(RootOperationOutcome.Succeeded, (await sibling.WaitAsync(HangGuard)).Outcome);
            Assert.AreEqual(RootOperationOutcome.Succeeded, (await source.StopWatchingAsync(CancellationToken.None)).Outcome);
        }

        Assert.AreEqual(mode == "sibling-before-stop" ? 1 : 2, watches.Starts.Count);
        var live = watches.Watches.Single(watch => watch.DisposeCount == 0);
        Assert.AreNotEqual(WatchState.NotStarted, volume.Status.Watch.State);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await volume.StopWatchingAsync(CancellationToken.None)).Outcome);
        Assert.AreEqual(1, live.DisposeCount);
    }

    [TestMethod]
    public async Task Start_StoppedWhileASiblingKeepsTheStart_ReportsItsWithdrawal()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        var gate = new TestGate();
        watches.HoldStart(gate, observeToken: false);
        var first = volume.StartWatchingAsync(CancellationToken.None);
        Task<RootOperationResult> sibling;
        try
        {
            await gate.Entered.WaitAsync(HangGuard);
            sibling = source.StartWatchingAsync(CancellationToken.None);
            Assert.AreEqual(RootOperationOutcome.Succeeded, (await volume.StopWatchingAsync(CancellationToken.None)).Outcome);
        }
        finally
        {
            gate.Release();
        }

        var withdrawn = await first.WaitAsync(HangGuard);
        Assert.AreEqual(RootOperationOutcome.Failed, withdrawn.Outcome);
        Assert.IsInstanceOfType<OperationCanceledException>(withdrawn.Failure);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await sibling.WaitAsync(HangGuard)).Outcome);
        Assert.IsFalse(volume.Status.Watch.IsRequested);
        Assert.IsTrue(source.Status.Watch.IsRequested);
    }

    [TestMethod]
    public async Task NonRequestingView_ReportsTheScansStateWithIsRequestedFalse()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.WaitForCatchUpAsync(CancellationToken.None);

        Assert.AreEqual(WatchState.CaughtUp, source.Status.Watch.State);
        Assert.IsFalse(source.Status.Watch.IsRequested);
        Assert.AreEqual(volume.Status.Watch.StateVersion, source.Status.Watch.StateVersion);
    }

    [TestMethod]
    public async Task Stop_WithAnotherRequest_KeepsTheWatch()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        await source.WaitForCatchUpAsync(CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, (await volume.StopWatchingAsync(CancellationToken.None)).Outcome);
        Assert.AreEqual(0, watches.Watches.Single().DisposeCount);
        Assert.AreEqual(WatchState.CaughtUp, source.Status.Watch.State);
        Assert.IsFalse(volume.Status.Watch.IsRequested);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await source.StopWatchingAsync(CancellationToken.None)).Outcome);
        Assert.AreEqual(1, watches.Watches.Single().DisposeCount);
        Assert.AreEqual(WatchState.NotStarted, source.Status.Watch.State);
    }

    [TestMethod]
    public async Task Stop_OfANonRequestingView_IsNotApplicableWhileAnotherWatches()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.NotApplicable, (await source.StopWatchingAsync(CancellationToken.None)).Outcome);
        Assert.AreEqual(0, watches.Watches.Single().DisposeCount);
    }

    [TestMethod]
    public async Task OutstandingFault_IsReturnedOncePerRequestingView()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await using var work = await fixture.OpenAsync(RootKey.FromDirectory(fixture.WorkPath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        var firstFault = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFault = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrequestedFaults = 0;
        volume.WatchFaulted += fault => firstFault.TrySetResult(fault);
        source.WatchFaulted += fault => secondFault.TrySetResult(fault);
        work.WatchFaulted += _ => Interlocked.Increment(ref unrequestedFaults);
        var failure = new IOException("synthetic channel failure");

        watches.Watches.Single().LoseChannel(failure);
        await Task.WhenAll(firstFault.Task, secondFault.Task).WaitAsync(HangGuard);

        Assert.AreSame(failure, (await volume.StopWatchingAsync(CancellationToken.None)).Failure);
        Assert.AreSame(failure, (await source.StopWatchingAsync(CancellationToken.None)).Failure);
        Assert.AreEqual(0, unrequestedFaults);
        Assert.AreEqual(RootOperationOutcome.NotApplicable, (await volume.StopWatchingAsync(CancellationToken.None)).Outcome);
        Assert.AreEqual(RootOperationOutcome.NotApplicable, (await source.StopWatchingAsync(CancellationToken.None)).Outcome);
    }

    [TestMethod]
    public async Task SubscriberFault_StaysOnTheThrowingView()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        var ownFault = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherFaults = 0;
        var changes = 0;
        var failure = new InvalidOperationException("synthetic subscriber failure");
        source.Changed += _ => throw failure;
        source.WatchFaulted += fault => ownFault.TrySetResult(fault);
        volume.WatchFaulted += _ => Interlocked.Increment(ref otherFaults);
        volume.Changed += _ => Interlocked.Increment(ref changes);

        await watches.Watches.Single().Publish(new JournalBatch(
            [SharedScanFixture.Record(40, 30, "new.txt", UsnReason.FileCreate)], 7, 5001));
        Assert.AreEqual(WatchFaultKind.Subscriber, (await ownFault.Task.WaitAsync(HangGuard)).Kind);
        Assert.AreEqual(0, otherFaults);
        Assert.AreEqual(1, changes);
        Assert.AreSame(failure, (await source.StopWatchingAsync(CancellationToken.None)).Failure);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await volume.StopWatchingAsync(CancellationToken.None)).Outcome);
    }
}
