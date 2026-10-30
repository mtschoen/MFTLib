using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     I6 Mapping: one mapping shared by every view's RootBlock. Scan teardown, retirement draining or
///     finalization releases it; closing an individual view never releases the mapping.
/// </summary>
sealed class MappedBlock(BlockFile block)
{
    internal BlockFile Block { get; } = block;
    internal RootBlockRelease ReleaseState { get; } = new(block);
    internal ProducerKind ProducerKind { get; } = block.Header.ProducerKind;
    internal ReportedReasonCycles ReportedCycles { get; } = new();
    internal bool IsReleased => ReleaseState.IsReleaseStarted;

    /// <summary>
    ///     Releases the block for a caller who simply dropped every handle. The catch is
    ///     deliberately unconditional and local to this path: releasing unmaps a view and can
    ///     delete a retired block file, so a file the operating system will not unlink surfaces as
    ///     an exception, and an exception leaving a finalizer terminates the process with a stack
    ///     no consumer can act on. The worst case swallowed here is a leaked mapping or a leftover
    ///     file, which is what the caller already has. <see cref="RootBlockRelease.DrainAndReleaseAsync" />
    ///     keeps propagating, because a deterministic teardown failure is worth surfacing.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1075",
        Justification = "A finalizer is the one place where catching everything and doing nothing is the " +
                        "correct behaviour: an exception that escapes it terminates the process. There is " +
                        "nothing to log to and nothing to retry, and the swallowed worst case is a leaked " +
                        "mapping, which is strictly better than the alternative. Scoped to this method only.")]
    ~MappedBlock()
    {
        try
        {
            ReleaseState.ReleaseUnborrowed();
        }
        catch (Exception)
        {
            // See this finalizer's summary.
        }
    }
}
