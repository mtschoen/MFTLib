using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     For tests whose subject is a watch fault's own bookkeeping rather than the recovery it
///     queues (spec 2.6.5): the drive's recovery stays queued, so the drive keeps its faulted
///     instance and reads <see cref="WatchState.Recovering" />.
/// </summary>
static class RecoveryHolds
{
    /// <summary>
    ///     Parks every recovery of <paramref name="index" /> before it takes its drive's lifecycle
    ///     gate, until the index's disposal cancels it.
    /// </summary>
    public static void HoldEveryRecovery(this FileIndex index)
    {
        index.Hooks.RecoveryBeforeLifecycleGate =
            static (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
