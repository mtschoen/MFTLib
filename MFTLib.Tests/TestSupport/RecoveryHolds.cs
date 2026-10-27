using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     For tests whose subject is a watch fault's own bookkeeping rather than the recovery it
///     queues (spec 2.6.5): the root's recovery stays queued, so the root keeps its faulted
///     instance and reads <see cref="WatchState.Recovering" />.
/// </summary>
static class RecoveryHolds
{
    /// <summary>
    ///     Parks every recovery of <paramref name="index" /> before it takes its root's lifecycle
    ///     gate, until the index's disposal cancels it.
    /// </summary>
    public static void HoldEveryRecovery(this FileIndex index)
    {
        index.Hooks.RecoveryBeforeLifecycleGate =
            static (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    public static void HoldEveryRecovery(this WatchHarness harness)
    {
        foreach (var index in harness.Indices)
        {
            index.HoldEveryRecovery();
        }
    }
}
