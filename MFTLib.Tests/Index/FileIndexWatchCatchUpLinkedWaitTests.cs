using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The no-caller-token branch of the catch-up wait: with <see cref="CancellationToken.None" />
///     the wait is linked to the index's disposal token only, so a clean catch-up completes it and
///     a disposal cancels it (cancelling the aggregate wait's coordinator with it).
/// </summary>
[TestClass]
public class FileIndexWatchCatchUpLinkedWaitTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task WaitForCatchUpAsync_WithoutACallerToken_CompletesWithTheCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var wait = harness.Index.WaitForCatchUpAsync('T', CancellationToken.None);
        Assert.IsFalse(wait.IsCompleted);

        await harness.PublishAsync(new DriveCaughtUp('T'));
        await wait.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.Index.Drives.Single().WatchCatchUp);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_WithoutACallerToken_WhenDisposed_CancelsTheWait()
    {
        var harness = new WatchHarness();
        try
        {
            await harness.Index.StartWatchingAsync(Token);
            await harness.SourceStartedAsync();

            var wait = harness.Index.WaitForCatchUpAsync('T', CancellationToken.None);
            Assert.IsFalse(wait.IsCompleted);

            await harness.Index.DisposeAsync();

            try
            {
                await wait.WaitAsync(FakeIndexWatchSource.HangGuard);
                Assert.Fail("Expected an OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected: the disposal token cancelled the linked wait.
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    /// <summary>
    ///     Disposal cancels the outer aggregate wait either way, so asserting only on that task
    ///     would pass even if the coordinator were never actually detached. This instead follows
    ///     the seam's own documented purpose - "a test can watch whether the coordinator is
    ///     collected after its wait ends" - and proves the coordinator's own slot registrations
    ///     were torn down by checking that the object itself becomes unreachable, the same
    ///     WeakReference-plus-GC pattern <see cref="SnapshotTests" /> uses for release proofs.
    /// </summary>
    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrivesWithoutACallerToken_WhenDisposed_CancelsTheCoordinator()
    {
        var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        try
        {
            var coordinatorReference = await RunCatchUpAndCaptureCoordinatorAsync(harness, Token);

            // The coordinator's completion source runs its continuations asynchronously
            // (RunContinuationsAsynchronously), so the wait observing cancellation and the
            // coordinator detaching its own slot registrations are two independent
            // continuations with no ordering guarantee between them: a single GC pass right
            // after the wait can still catch the coordinator mid-detachment. Poll rather than
            // assume the detachment already landed.
            var collected = false;
            var deadline = DateTime.UtcNow + FakeIndexWatchSource.HangGuard;
            while (DateTime.UtcNow < deadline)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (!coordinatorReference.TryGetTarget(out _))
                {
                    collected = true;
                    break;
                }

                await Task.Delay(20);
            }

            Assert.IsTrue(collected,
                "disposal must detach the coordinator's slot registrations, not merely cancel the outer wait");
        }
        finally
        {
            harness.Dispose();
        }
    }

    /// <summary>
    ///     Isolated in its own non-inlined method so the coordinator instance is reachable only
    ///     through the returned <see cref="WeakReference{T}" /> once this call returns - an async
    ///     method's hoisted locals can otherwise stay pinned in the caller's own state machine for
    ///     the rest of its execution, keeping the object artificially alive past its last use.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<WeakReference<object>> RunCatchUpAndCaptureCoordinatorAsync(
        WatchHarness harness, CancellationToken token)
    {
        object? coordinator = null;
        var creationCount = 0;
        harness.Index._catchUpCoordinatorCreatedForTest = created =>
        {
            coordinator = created;
            creationCount++;
        };
        try
        {
            await harness.Index.StartWatchingAsync(token);
            await harness.SourceStartedAsync();

            var wait = harness.Index.WaitForCatchUpAsync(CancellationToken.None);
            Assert.IsFalse(wait.IsCompleted);
            Assert.AreEqual(1, creationCount,
                "a two-drive aggregate wait is coordinated, unlike the single-drive fast path");

            await harness.Index.DisposeAsync();

            try
            {
                await wait.WaitAsync(FakeIndexWatchSource.HangGuard, token);
                Assert.Fail("Expected an OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected: the disposal token cancelled the linked wait.
            }

            return new WeakReference<object>(coordinator!);
        }
        finally
        {
            harness.Index._catchUpCoordinatorCreatedForTest = null;
        }
    }
}
