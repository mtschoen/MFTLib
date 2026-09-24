namespace MFTLib.Index;

/// <summary>
///     Thrown by <see cref="IIndexWatchSource.ArmDriveAsync" /> and
///     <see cref="IIndexWatchSource.DisarmDriveAsync" /> when the source has no stream running,
///     either because none has started yet or because the last one has already been released.
///     <see cref="FileIndex" /> uses the type, not the message, to tell a stream that has gone
///     away from a drive operation that failed on a stream still running.
/// </summary>
public sealed class WatchStreamNotRunningException : InvalidOperationException
{
    public WatchStreamNotRunningException()
    {
    }

    public WatchStreamNotRunningException(string message)
        : base(message)
    {
    }

    public WatchStreamNotRunningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
