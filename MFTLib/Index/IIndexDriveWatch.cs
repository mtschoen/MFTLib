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
    IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken);
}
