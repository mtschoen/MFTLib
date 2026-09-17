using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Guards against aggregate catch-up waits leaking their coordinators: a wait that ended by
///     cancellation must not stay reachable from a drive slot that is still catching up. Drive T
///     is caught up before the waits start, so drive U's pending slot is the only thing a
///     coordinator could still be attached to.
/// </summary>
[TestClass]
public class FileIndexWatchCatchUpRetentionTests
{
    const int WaitCount = 50;
    const int MaximumCollectionAttempts = 40;
    static readonly TimeSpan CollectionPollInterval = TimeSpan.FromMilliseconds(25);

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_CanceledWaitsReleaseTheirCoordinatorsWhileADriveIsPending()
    {
        using var harness = await StartWatchWithOneDrivePendingAsync();

        var coordinators = await RunCanceledAggregateWaitsAsync(harness.Index);

        Assert.AreEqual(WatchCatchUpState.CatchingUp, CatchUpStateOf(harness, 'U'));
        Assert.AreEqual(0, await CountSurvivorsAsync(coordinators),
            "Canceled aggregate waits stayed reachable from the pending drive's slot.");
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_AllDrives_CanceledWaitsReleaseTheirCoordinatorsOnceThePendingDriveCatchesUp()
    {
        using var harness = await StartWatchWithOneDrivePendingAsync();
        var coordinators = await RunCanceledAggregateWaitsAsync(harness.Index);

        await harness.PublishAsync(new DriveCaughtUp('U'));

        Assert.AreEqual(WatchCatchUpState.CaughtUp, CatchUpStateOf(harness, 'U'));
        Assert.AreEqual(0, await CountSurvivorsAsync(coordinators),
            "The harness cannot observe collection: coordinators survived with no pending slot left.");
    }

    async Task<WatchHarness> StartWatchWithOneDrivePendingAsync()
    {
        var harness = new WatchHarness(
            [new IndexWatchTarget('T', 7, 100), new IndexWatchTarget('U', 7, 100)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveCaughtUp('T'));
        return harness;
    }

    static WatchCatchUpState CatchUpStateOf(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(drive => drive.DriveLetter == driveLetter).WatchCatchUp;
    }

    static async Task<WeakReference[]> RunCanceledAggregateWaitsAsync(FileIndex index)
    {
        var (coordinators, waits) = StartCanceledAggregateWaits(index);
        foreach (var wait in waits)
        {
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => wait);
            Assert.IsTrue(wait.IsCanceled);
        }

        return coordinators;
    }

    /// <summary>
    ///     Kept out of line so no JIT-extended local roots a coordinator past this frame: only the
    ///     weak references and the wait tasks leave it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference[] Coordinators, Task[] Waits) StartCanceledAggregateWaits(FileIndex index)
    {
        var coordinators = new List<WeakReference>(WaitCount);
        var waits = new Task[WaitCount];
        index._catchUpCoordinatorCreatedForTest = coordinator => coordinators.Add(new WeakReference(coordinator));
        try
        {
            for (var waitIndex = 0; waitIndex < WaitCount; waitIndex++)
            {
                using var caller = new CancellationTokenSource();
                waits[waitIndex] = index.WaitForCatchUpAsync(caller.Token);
                caller.Cancel();
            }
        }
        finally
        {
            index._catchUpCoordinatorCreatedForTest = null;
        }

        Assert.AreEqual(WaitCount, coordinators.Count);
        return (coordinators.ToArray(), waits);
    }

    /// <summary>
    ///     Forces full blocking collections until every coordinator is gone or the attempts run
    ///     out. Polling covers continuations that were queued to the thread pool and have not run
    ///     yet, which still hold their coordinator until they do.
    /// </summary>
    static async Task<int> CountSurvivorsAsync(WeakReference[] coordinators)
    {
        for (var attempt = 0; attempt < MaximumCollectionAttempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!coordinators.Any(coordinator => coordinator.IsAlive))
            {
                return 0;
            }

            await Task.Delay(CollectionPollInterval);
        }

        return coordinators.Count(coordinator => coordinator.IsAlive);
    }
}
