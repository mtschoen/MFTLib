using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     One mapping of a block lineage: the mapped <see cref="BlockFile" />, the release state that counts its
///     readers, and the finalizer that releases it once nothing reaches it. A lineage (<see cref="MappedBlock" />)
///     publishes one mapping at a time as <see cref="MappedBlock.Current" /> and keeps every mapping it retired, so
///     a mapping stays reachable for as long as its lineage is.
/// </summary>
sealed class BlockMapping(BlockFile block)
{
    internal BlockFile Block { get; } = block;
    internal RootBlockRelease ReleaseState { get; } = new(block);
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
    ~BlockMapping()
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
