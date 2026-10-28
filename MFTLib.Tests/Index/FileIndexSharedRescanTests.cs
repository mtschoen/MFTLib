using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSharedRescanTests
{
    [TestMethod]
    public async Task Rescan_TwoConcurrentCallsWhileIdle_ProduceOnce()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var held = await HoldStartAsync(watches, volume);
        var first = volume.RescanAsync(CancellationToken.None);
        var second = source.RescanAsync(CancellationToken.None);
        held.Gate.Release();
        await held.Start;

        var results = await Task.WhenAll(first, second).WaitAsync(HangGuard);

        Assert.IsTrue(results.All(result => result.Outcome == RootOperationOutcome.Succeeded));
        Assert.AreEqual(2, fixture.ProductionCount);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Rescan_LateCallers_GetOneLaterProduction(int laterCallers)
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var gate = fixture.HoldProduction();
        var first = volume.RescanAsync(CancellationToken.None);
        await gate.Entered.WaitAsync(HangGuard);
        var later = Enumerable.Range(0, laterCallers).Select(_ => source.RescanAsync(CancellationToken.None)).ToArray();
        gate.Release();

        var results = await Task.WhenAll(later.Append(first)).WaitAsync(HangGuard);

        Assert.IsTrue(results.All(result => result.Outcome == RootOperationOutcome.Succeeded));
        Assert.AreEqual(3, fixture.ProductionCount, "one running rescan and one pending production");
    }

    [TestMethod]
    public async Task Rescan_OneOfTwoCallersCancels_ProductionContinues()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        using var cancellation = new CancellationTokenSource();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var held = await HoldStartAsync(watches, volume);
        var first = volume.RescanAsync(cancellation.Token);
        var second = source.RescanAsync(CancellationToken.None);
        var production = fixture.HoldProduction();
        held.Gate.Release();
        await held.Start;
        try
        {
            await production.Entered.WaitAsync(HangGuard);
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first);
            Assert.IsFalse(fixture.LastProductionToken.IsCancellationRequested);
        }
        finally
        {
            production.Release();
        }
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await second).Outcome);
        Assert.AreEqual(2, fixture.ProductionCount);
    }

    [TestMethod]
    public async Task Rescan_EveryCallerCancels_CancelsTheProduction()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        using var cancellation = new CancellationTokenSource();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var original = volume.CurrentBlock;
        var held = await HoldStartAsync(watches, volume);
        var first = volume.RescanAsync(cancellation.Token);
        var second = source.RescanAsync(cancellation.Token);
        var production = fixture.HoldProduction();
        held.Gate.Release();
        await held.Start;
        try
        {
            await production.Entered.WaitAsync(HangGuard);
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => second);
            Assert.IsTrue(fixture.LastProductionToken.IsCancellationRequested);
            Assert.AreSame(original, volume.CurrentBlock);
        }
        finally
        {
            production.Release();
        }
    }

    [TestMethod]
    public async Task Rescan_ViewDisposedWhileWaiting_EndsOnlyItsWait()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var held = await HoldStartAsync(watches, volume);
        var first = volume.RescanAsync(CancellationToken.None);
        var second = source.RescanAsync(CancellationToken.None);
        var production = fixture.HoldProduction();
        held.Gate.Release();
        await held.Start;
        try
        {
            await production.Entered.WaitAsync(HangGuard);
            // ReSharper disable once DisposeOnUsingVariable -- Disposal during an admitted rescan is the act; await using covers failure cleanup.
            await volume.DisposeAsync();
            var disposed = await first.WaitAsync(HangGuard);
            Assert.IsInstanceOfType<OperationCanceledException>(disposed.Failure);
            Assert.IsFalse(fixture.LastProductionToken.IsCancellationRequested);
        }
        finally
        {
            production.Release();
        }
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await second).Outcome);
        Assert.AreEqual(2, fixture.ProductionCount);
    }

    [TestMethod]
    public async Task Rescan_FailedProduction_EveryCallerGetsTheSameFailure()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var held = await HoldStartAsync(watches, volume);
        fixture.BeforeProduction = () => throw new IOException("shared production failure");
        var first = volume.RescanAsync(CancellationToken.None);
        var second = source.RescanAsync(CancellationToken.None);
        held.Gate.Release();
        await held.Start;

        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(RootOperationOutcome.Failed, results[0].Outcome);
        Assert.AreSame(results[0].Failure, results[1].Failure);
        Assert.AreEqual(2, fixture.ProductionCount);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(3, true)]
    public async Task CheckpointLoss_IsReportedOnBothViews(int lossCount, bool stopped)
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        var losses = new Queue<JournalCheckpointLoss?>(Enumerable.Repeat<JournalCheckpointLoss?>(
            CatchUpLossTestSupport.StandardCatchUpLoss('T'), lossCount).Append(null));
        fixture.CatchUpLoss = losses.Dequeue;
        var firstFaults = new List<WatchFault>();
        var secondFaults = new List<WatchFault>();
        volume.WatchFaulted += firstFaults.Add;
        source.WatchFaulted += secondFaults.Add;

        await volume.RescanAsync(CancellationToken.None);

        Assert.AreEqual(volume.Status.Watch.CheckpointLoss, source.Status.Watch.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, source.Status.Watch.CheckpointLoss!.DetectedDuring);
        Assert.AreEqual(stopped, source.Status.Watch.IsRecoveryStopped);
        Assert.AreEqual(volume.Status.Watch.ConsecutiveLostCatchUps, source.Status.Watch.ConsecutiveLostCatchUps);
        Assert.HasCount(lossCount, firstFaults);
        Assert.HasCount(lossCount, secondFaults);
    }

    [TestMethod]
    public async Task Generation_IsOneValuePerBlock()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var initial = volume.Status.Block!.Generation;

        await SharedScanFixture.ApplyAsync(source, SharedScanFixture.Record(31, 30, "a.txt", UsnReason.DataOverwrite));
        var changed = source.Status.Block!.Generation;
        Assert.IsGreaterThan(initial, changed);
        Assert.AreEqual(changed, volume.Status.Block.Generation);
        await volume.RescanAsync(CancellationToken.None);

        Assert.IsGreaterThan(changed, volume.Status.Block.Generation);
        Assert.AreEqual(volume.Status.Block.Generation, source.Status.Block!.Generation);
    }

    static async Task<(TestGate Gate, Task<RootOperationResult> Start)> HoldStartAsync(ScriptedWatchSource watches, FileIndex index)
    {
        var gate = new TestGate();
        watches.HoldStart(gate);
        var start = index.StartWatchingAsync(CancellationToken.None);
        await gate.Entered.WaitAsync(HangGuard);
        return (gate, start);
    }
}
