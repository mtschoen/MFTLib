namespace MFTLib.Index;

/// <summary>
///     One root's live watch, as <see cref="IIndexWatchSource.StartAsync" /> returns it.
///     <see cref="FileIndex" /> reads it from one pump and disposes it exactly once: from that
///     pump's exit path once the handle is published, or from the start path when the root was
///     stopped before the handle could be published. An implementation therefore need not make
///     <see cref="IAsyncDisposable.DisposeAsync" /> idempotent, and it completes once nothing
///     further for the root can be read.
/// </summary>
interface IIndexVolumeWatch : IAsyncDisposable
{
    /// <summary>
    ///     Yields the root's <see cref="JournalBatch" /> items, and one <see cref="VolumeCaughtUp" />
    ///     once the backlog present when the watch started has been delivered (immediately when
    ///     there is none). Cancelling <paramref name="cancellationToken" /> ends the enumeration
    ///     promptly; that is how the index stops a root's pump. Otherwise the enumeration ends only
    ///     by throwing: <see cref="VolumeWatchFaultException" /> when the root's own watch failed,
    ///     any other exception when the channel carrying it was lost. A normal end before
    ///     cancellation is treated as a lost channel.
    /// </summary>
    /// <param name="cancellationToken">Token used by the index to stop this root's pump.</param>
    /// <returns>An asynchronous sequence of journal batches and the caught-up marker.</returns>
    /// <exception cref="VolumeWatchFaultException">The root's own watch reports a fault.</exception>
    IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken);
}
