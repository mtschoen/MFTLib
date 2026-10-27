namespace MFTLib.Index;

/// <summary>
///     Starts the live watch of one root and hands back the handle that is that root's watch:
///     reading the handle yields the root's items, and disposing it stops the root. Every root
///     an index watches has its own handle, so nothing one root does can end, delay, or fault
///     another root's watch.
/// </summary>
interface IIndexWatchSource
{
    /// <summary>
    ///     Starts watching <paramref name="target" />'s root from its cursor and returns once the
    ///     watch is ready to be read. There is no separate readiness signal: a returned handle is
    ///     a running watch. The source observes <paramref name="cancellationToken" /> until it
    ///     returns, and a start cancelled after it has acquired anything releases it before
    ///     throwing. <see cref="FileIndex" /> cancels the token when the root is stopped, or the
    ///     index disposed, while the start is still in progress; a handle returned after that is
    ///     disposed by the index without being read.
    /// </summary>
    Task<IIndexVolumeWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken);
}
