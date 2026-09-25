namespace MFTLib.Index;

/// <summary>
///     Supplies one merged stream of watch items for the drives an index watches, and arms and
///     disarms individual drives on that stream while it runs. One instance runs at most one
///     stream at a time: starting a stream throws <see cref="InvalidOperationException" /> when
///     one is already live, and the two per-drive members throw it when none is. The interface
///     declares no disposal member and needs none: the index ends a stream by cancelling the token
///     it passed when starting it, which runs the implementation's own cleanup.
///     <para>
///         <see cref="FileIndex" /> always starts a stream through
///         <see cref="StartWatching(IReadOnlyList{IndexWatchTarget}, Action, CancellationToken)" />,
///         and <see cref="FileIndex.StartWatchingAsync" /> completes only once that stream has
///         reported it is ready. How strong that guarantee is depends on the source: one that
///         overrides that overload reports readiness itself, and one that implements only
///         <see cref="StartWatching(IReadOnlyList{IndexWatchTarget}, CancellationToken)" /> gets
///         the default, which reports readiness once the stream's first
///         <see cref="IAsyncEnumerator{T}.MoveNextAsync" /> call has returned control.
///     </para>
/// </summary>
public interface IIndexWatchSource
{
    /// <summary>
    ///     Yields every watched drive's batches on one stream, resuming each drive from its
    ///     target's cursor. A source yields one <see cref="DriveCaughtUp" /> per arm once the
    ///     backlog present at that arm has been delivered, immediately when there is none. A drive
    ///     whose watch fails yields one <see cref="DriveWatchFailure" /> instead of ever yielding
    ///     its marker and stops being read, leaving every other drive flowing. The stream
    ///     completes when <paramref name="cancellationToken" /> is cancelled or when every drive
    ///     it was reading has yielded its failure, and it throws only when the source cannot start
    ///     at all or when a failure cannot be attributed to one drive. Completing it while a drive
    ///     is still being watched is a source fault: the index has no drive left to watch and no
    ///     failure to explain it, so it announces the end as a <see cref="WatchFaultKind.Source" />
    ///     fault against every watched drive. Two targets naming one drive are rejected with an
    ///     <see cref="ArgumentException" /> before the source starts. The index cancels
    ///     <paramref name="cancellationToken" /> when the session stops and the implementation
    ///     must then finish, since a source that ignores its token wedges
    ///     <see cref="FileIndex.StopWatchingAsync" /> until that call's own token bounds the wait.
    ///     The same holds before the stream is ready: a start that is cancelled, stopped, or
    ///     disposed waits for the stream to end before it returns, so a source must observe the
    ///     token at every await during startup too, and hand off any step it must not interrupt
    ///     rather than wait for it, as <see cref="BrokerIndexWatchSource" /> does with its
    ///     StartWatch send.
    /// </summary>
    IAsyncEnumerable<WatchStreamItem> StartWatching(
        IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken);

    /// <summary>
    ///     Starts the same stream as
    ///     <see cref="StartWatching(IReadOnlyList{IndexWatchTarget}, CancellationToken)" /> and
    ///     calls <paramref name="reportStreamReady" /> once the stream accepts
    ///     <see cref="ArmDriveAsync" /> and <see cref="DisarmDriveAsync" />, which is the moment
    ///     <see cref="FileIndex.StartWatchingAsync" /> waits for. Readiness is about per-drive
    ///     control, not data: a source reports it without having yielded anything and without
    ///     any drive having caught up, which <see cref="FileIndex.WaitForCatchUpAsync(CancellationToken)" />
    ///     waits for separately. The callback belongs to this one stream, may be called from any
    ///     thread, and may be called more than once; only the first call counts. A source that
    ///     cannot become ready throws from the stream, or ends it, instead of calling it; the index
    ///     then fails the start with that exception. Yielding an item also counts as ready, since a
    ///     source cannot yield without a running stream.
    ///     <para>
    ///         The default implementation calls
    ///         <see cref="StartWatching(IReadOnlyList{IndexWatchTarget}, CancellationToken)" /> and
    ///         reports readiness as soon as the stream's first
    ///         <see cref="IAsyncEnumerator{T}.MoveNextAsync" /> call returns control with the stream
    ///         still running: the call is still pending, or it has already produced an item. A
    ///         pending call is an async iterator that has run its code up to its first incomplete
    ///         await, so a source that makes itself ready before that point, as a source over an
    ///         in-memory queue does, is ready when reported. A first call that has already ended the
    ///         stream or faulted reports nothing, so the start fails with that end or fault. A
    ///         source that awaits anything, such as a connection, before accepting per-drive calls
    ///         must override this member and report readiness itself: the default cannot see past
    ///         that await, so a per-drive call made in that window can still be rejected, and a
    ///         failure or end after it arrives as a fault of a running session, not of the start.
    ///     </para>
    /// </summary>
    IAsyncEnumerable<WatchStreamItem> StartWatching(
        IReadOnlyList<IndexWatchTarget> targets, Action reportStreamReady, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportStreamReady);
        return new ReadyOnFirstMoveWatchStream(StartWatching(targets, cancellationToken), reportStreamReady);
    }

    /// <summary>
    ///     Adds or replaces one drive on the running stream, resuming it from
    ///     <paramref name="target" />'s cursor and yielding nothing that was produced before the
    ///     arm. Replacing a drive that is already armed retires the old reader before starting
    ///     the fresh one, and must do so without ending any other drive's watch.
    ///     <paramref name="cancellationToken" /> bounds the wait for a retiring reader, so a
    ///     wedged reader surfaces as a cancellation rather than a hang. Throws
    ///     <see cref="WatchStreamNotRunningException" /> when no stream is running.
    /// </summary>
    Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken);

    /// <summary>
    ///     Stops one drive's items. This is not a failure and yields no
    ///     <see cref="DriveWatchFailure" />; it completes only once nothing further for that
    ///     drive can reach the stream. <paramref name="cancellationToken" /> bounds the wait for
    ///     the retiring reader, for the same reason <see cref="ArmDriveAsync" /> takes one. Throws
    ///     <see cref="WatchStreamNotRunningException" /> when no stream is running.
    /// </summary>
    Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken);

    /// <summary>
    ///     Requests that the current watch stream begin stopping with the caller's cancellation
    ///     token bounding the stop acknowledgement wait.
    ///     The default implementation does nothing.
    /// </summary>
    void RequestStop(CancellationToken cancellationToken)
    {
    }
}
