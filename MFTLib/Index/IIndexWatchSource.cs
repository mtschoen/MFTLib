namespace MFTLib.Index;

/// <summary>
///     Supplies one merged stream of watch items for the drives an index watches, and arms and
///     disarms individual drives on that stream while it runs. One instance runs at most one
///     stream at a time: <see cref="StartWatching" /> throws
///     <see cref="InvalidOperationException" /> when one is already live, and the two per-drive
///     members throw it when none is. The interface declares no disposal member and needs none:
///     the index ends a stream by cancelling the token it passed to <see cref="StartWatching" />,
///     which runs the implementation's own cleanup.
/// </summary>
public interface IIndexWatchSource
{
    /// <summary>
    ///     Yields every watched drive's batches on one stream, resuming each drive from its
    ///     target's cursor. A drive whose watch fails yields one
    ///     <see cref="DriveWatchFailure" /> and stops being read, leaving every other drive
    ///     flowing. The stream completes when <paramref name="cancellationToken" /> is cancelled
    ///     or when every drive it was reading has yielded its failure, and it throws only when the
    ///     source cannot start at all or when a failure cannot be attributed to one drive.
    ///     Completing it while a drive is still being watched is a source fault: the index has no
    ///     drive left to watch and no failure to explain it, so it announces the end as a
    ///     <see cref="WatchFaultKind.Source" /> fault against every watched drive. Two targets
    ///     naming one drive are rejected with an <see cref="ArgumentException" /> before the
    ///     source starts. The index cancels <paramref name="cancellationToken" /> when the
    ///     session stops and the implementation must then finish, since a source that ignores its
    ///     token wedges <see cref="FileIndex.StopWatchingAsync" /> until that call's own token
    ///     bounds the wait.
    /// </summary>
    IAsyncEnumerable<WatchStreamItem> StartWatching(
        IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken);

    /// <summary>
    ///     Adds or replaces one drive on the running stream, resuming it from
    ///     <paramref name="target" />'s cursor and yielding nothing that was produced before the
    ///     arm. Replacing a drive that is already armed retires the old reader before starting
    ///     the fresh one, and must do so without ending any other drive's watch.
    ///     <paramref name="cancellationToken" /> bounds the wait for a retiring reader, so a
    ///     wedged reader surfaces as a cancellation rather than a hang.
    /// </summary>
    Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken);

    /// <summary>
    ///     Stops one drive's items. This is not a failure and yields no
    ///     <see cref="DriveWatchFailure" />; it completes only once nothing further for that
    ///     drive can reach the stream. <paramref name="cancellationToken" /> bounds the wait for
    ///     the retiring reader, for the same reason <see cref="ArmDriveAsync" /> takes one.
    /// </summary>
    Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken);
}
