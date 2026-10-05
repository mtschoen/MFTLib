using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A start, a rescan's retry after a lost catch-up, or a rescan's restart of a watch, that was
///     admitted before <see cref="FileIndex.DisposeAsync" /> began and reaches its next checkpoint
///     after disposal set its flag is cancelled by the disposal: it ends with
///     <see cref="OperationCanceledException" />, never with the <see cref="ObjectDisposedException" />
///     a call made after disposal gets. Each test lets the operation reach its checkpoint while the
///     flag is set and before the disposal token is cancelled, which is the window a linked token
///     cannot yet report. A recovery in the same window ends quietly instead, since disposal
///     never reports a watch's fault.
/// </summary>
[TestClass]
public class FileIndexAdmittedOperationDisposalTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StartWatchingAsync_QueuedOnTheGateWhenDisposalSetsItsFlag_IsCancelled()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        var production = harness.HoldNextProduction('T');
        var gateHolder = index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(HangGuard);
        var start = index.StartWatchingAsync('T', Token);
        var settledInTheWindow = false;
        index.DisposedFlagSetForTest = () =>
        {
            production.Release();
            try
            {
                settledInTheWindow = Task.WaitAll([gateHolder, start], HangGuard);
            }
            catch (AggregateException)
            {
                // Both are expected to fault; their exceptions are asserted below.
                settledInTheWindow = true;
            }
        };

        var disposal = index.DisposeAsync().AsTask();

        Assert.IsTrue(settledInTheWindow, "the rescan and the start settled before disposal cancelled its token");
        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => gateHolder).WaitAsync(HangGuard);
        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => start).WaitAsync(HangGuard);
        await disposal.WaitAsync(HangGuard);
        Assert.AreEqual(0, harness.Source.TargetsFor('T').Count, "the cancelled start never invoked the source");
    }

    /// <summary>
    ///     Disposal sets its flag while the rescan raises the lost catch-up of its first scan; the
    ///     retry that follows is cancelled at its first checkpoint instead of scanning again.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_DisposalBeginsBeforeTheRetryAfterALostCatchUp_IsCancelled()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        harness.ScriptScans('T', new WatchHarnessScan(new JournalCheckpointLoss
        {
            DriveLetter = 'T',
            DetectedDuring = JournalCheckpointLossDetection.ScanCatchUp,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 1000,
            FirstUsn = 5000,
            NextUsn = 9000,
            AllocationDelta = 4096,
            MaximumSize = 32768,
            BytesBehind = 4000,
            SizeThatWouldHaveRetained = 12288
        }), new WatchHarnessScan());
        var lostRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flagSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.DisposedFlagSetForTest = () => flagSet.TrySetResult();
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.CatchUpLost)
            {
                // Holds the rescan between its lost catch-up and its retry until disposal has set
                // its flag; the retry's first checkpoint then runs after the flag is set.
                lostRaised.TrySetResult();
                TestGate.WaitSynchronously(flagSet.Task);
            }
        };
        var producedBefore = harness.ProductionCount('T');
        var rescan = Task.Run(() => index.RescanAsync('T', Token));
        await lostRaised.Task.WaitAsync(HangGuard);

        var disposal = index.DisposeAsync().AsTask();

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => rescan).WaitAsync(HangGuard);
        await disposal.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the retry never scanned");
    }

    /// <summary>
    ///     Disposal sets its flag after the rescan published its block and before its restart
    ///     decision reads the flag; the rescan is cancelled rather than completing without its
    ///     restart.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_DisposalBeginsBeforeTheRestartDecision_IsCancelled()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var decisionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flagSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.DisposedFlagSetForTest = () => flagSet.TrySetResult();
        index.BeforeRestartDecisionForTest = _ =>
        {
            decisionReached.TrySetResult();
            TestGate.WaitSynchronously(flagSet.Task);
        };
        var rescan = Task.Run(() => index.RescanAsync('T', Token));
        await decisionReached.Task.WaitAsync(HangGuard);

        await index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => rescan).WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the restart never invoked the source");
    }

    /// <summary>
    ///     The same window for a recovery: disposal sets its flag after the recovery published its
    ///     block and before its restart decision, and the recovery ends quietly, raising no
    ///     <see cref="WatchFaultKind.Recovery" /> fault and restarting nothing.
    /// </summary>
    [TestMethod]
    public async Task Recovery_DisposalBeginsBeforeTheRestartDecision_EndsWithoutAFault()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        var decisionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flagSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.DisposedFlagSetForTest = () => flagSet.TrySetResult();
        index.BeforeRestartDecisionForTest = _ =>
        {
            decisionReached.TrySetResult();
            TestGate.WaitSynchronously(flagSet.Task);
        };
        var producedBefore = harness.ProductionCount('T');
        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await decisionReached.Task.WaitAsync(HangGuard);
        var recovery = harness.RecoveryCompletion('T');

        await index.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await recovery.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.ProductionCount('T') - producedBefore, "the recovery scanned once");
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the recovery restarted nothing");
        CollectionAssert.AreEqual(new[] { WatchFaultKind.Drive },
            harness.Faults.Where(fault => fault.DriveLetter == 'T').Select(fault => fault.Kind).ToArray());
    }

    [TestMethod]
    public async Task RescanAsync_DisposalBeginsAfterTheRestartDecision_IsCancelled()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Task? disposal = null;
        index.RestartRequestedForTest = _ => disposal ??= index.DisposeAsync().AsTask();

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(
            () => index.RescanAsync('T', Token)).WaitAsync(HangGuard);

        Assert.IsNotNull(disposal, "the restart decided to start the watch again");
        await disposal.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the restart never invoked the source");
    }

    [TestMethod]
    public async Task RescanAsync_DisposalBeginsBeforeTheRestartRegisters_IsCancelled()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        await index.StartWatchingAsync('T', Token).WaitAsync(HangGuard);
        Task? disposal = null;
        index.RestartBeforeRegistrationForTest = _ =>
        {
            disposal ??= index.DisposeAsync().AsTask();
            return Task.CompletedTask;
        };

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(
            () => index.RescanAsync('T', Token)).WaitAsync(HangGuard);

        Assert.IsNotNull(disposal, "the restart reached its registration");
        await disposal.WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Source.TargetsFor('T').Count, "the restart never invoked the source");
    }
}
