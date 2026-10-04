namespace MFTLib.Index;

/// <summary>
///     One drive's live watch, as <see cref="IIndexWatchSource.StartAsync" /> returns it.
///     <see cref="FileIndex" /> reads it from one pump and disposes it exactly once: from that
///     pump's exit path once the handle is published, or from the start path when the drive was
///     stopped before the handle could be published. An implementation therefore need not make
///     <see cref="IAsyncDisposable.DisposeAsync" /> idempotent, and it completes once nothing
///     further for the drive can be read.
/// </summary>
public interface IIndexDriveWatch : IAsyncDisposable
{
    /// <summary>Gets the drive the index uses to associate this watch's batches and faults with a drive block.</summary>
    char DriveLetter { get; }

    /// <summary>
    ///     Yields the drive's <see cref="JournalBatch" /> items, and one <see cref="DriveCaughtUp" />
    ///     once the backlog present when the watch started has been delivered (immediately when
    ///     there is none). Cancelling <paramref name="cancellationToken" /> ends the enumeration
    ///     promptly; that is how the index stops a drive's pump. Otherwise the enumeration ends only
    ///     by throwing: <see cref="DriveWatchFaultException" /> when the drive's own watch failed,
    ///     any other exception when the channel carrying it was lost. A normal end before
    ///     cancellation is treated as a lost channel.
    /// </summary>
    /// <param name="cancellationToken">Token used by the index to stop this drive's pump.</param>
    /// <returns>An asynchronous sequence of journal batches and the caught-up marker.</returns>
    /// <exception cref="DriveWatchFaultException">The drive's own watch reports a fault.</exception>
    IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken);
}
