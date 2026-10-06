namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Raised once for every change of a drive's <see cref="DriveStatus.WatchCatchUpState" />, with
    ///     the drive's new state and its new <see cref="DriveStatus.WatchStateVersion" />: a start,
    ///     a catch-up, a fault, a recovery being queued, finishing (the restarted watch reads
    ///     <see cref="WatchCatchUpState.CatchingUp" />, then <see cref="WatchCatchUpState.CaughtUp" />)
    ///     or failing, a rescan retiring and replacing the watch, a lost catch-up, a refused start,
    ///     a stop, and disposal. Every version of a drive is delivered exactly once, after the change
    ///     is visible in <see cref="Drives" />, by a thread that changed the drive's state: a drive's
    ///     pump, a scan or recovery holding the drive's lifecycle gate, or the caller of a lifecycle
    ///     method, including <see cref="DisposeAsync" />, which delivers it before that method
    ///     returns. No state lock or write gate is held during delivery. A change a fault caused is
    ///     delivered before <see cref="WatchFaulted" /> reports that fault. Once
    ///     <see cref="DisposeAsync" /> has begun, <see cref="Drives" /> throws
    ///     <see cref="ObjectDisposedException" />, so a handler of a disposal change reads the event
    ///     itself.
    ///     <para>
    ///         One drive's changes are delivered one at a time, in version order; different drives'
    ///         changes can be delivered concurrently. A handler must therefore not block waiting for
    ///         another change of the same drive. A consumer that also reads <see cref="Drives" />,
    ///         for example to seed a subscription made after the index opened, applies an event only
    ///         when its <see cref="DriveWatchState.WatchStateVersion" /> is larger than the last one it applied
    ///         for that drive, since its read can be newer than an event still being delivered. A
    ///         decision a consumer tagged with a version is superseded by any event of the same drive
    ///         with a larger one, which is how it orders its own publication against a fault that
    ///         lands after it read the state.
    ///     </para>
    ///     <para>
    ///         The reentrancy rules of <see cref="WatchFaulted" /> apply: lifecycle calls of this
    ///         index fail at once inside a handler, which must queue them instead. Exceptions
    ///         thrown by handlers are discarded, and every subscriber still receives every change.
    ///     </para>
    /// </summary>
    public event Action<DriveWatchState>? WatchStateChanged;

    /// <summary>
    ///     The one place a drive's watch state change is recorded: derives the drive's state, and
    ///     when it differs from the state last noted, bumps the drive's version and queues the
    ///     change, with the fault that caused it, for <see cref="RaiseWatchStateChanged" />. Every
    ///     section that changes an input of <see cref="GetWatchCatchUpStateLocked" /> calls this
    ///     before it releases the lock; a call that finds no change does nothing. The caller holds
    ///     <see cref="_stateLock" />.
    /// </summary>
    void NoteWatchStateLocked(DriveRuntime runtime, WatchFault? cause = null)
    {
        var state = GetWatchCatchUpStateLocked(runtime.DriveLetter);
        if (state == runtime.NotedWatchState)
        {
            return;
        }

        runtime.NotedWatchState = state;
        runtime.WatchStateVersion++;
        (runtime.PendingWatchStates ??= []).Add(
            new DriveWatchState(runtime.DriveLetter, state, runtime.WatchStateVersion, cause));
    }

    /// <summary>
    ///     Delivers the drive's noted changes to every subscriber, in version order, isolating
    ///     each handler's failure. Called with no state lock or write gate held after every section
    ///     that notes a change, and by <see cref="RaiseWatchFaulted" /> before it reports a fault.
    ///     The drive's <see cref="DriveRuntime.DeliveryLock" /> is held from taking the queue until
    ///     its last handler returns, so a caller that finds the queue empty returns only once
    ///     every change noted before it has been delivered: that is what keeps a fault's
    ///     <see cref="WatchFaulted" /> behind the state change it caused when another thread took
    ///     the queue first.
    /// </summary>
    void RaiseWatchStateChanged(DriveRuntime runtime)
    {
        if (!runtime.DeliveryLock.TryEnter())
        {
            Interlocked.Increment(ref _contendedWatchStateDeliveries);
            runtime.DeliveryLock.Enter();
        }

        try
        {
            List<DriveWatchState>? pending;
            lock (_stateLock)
            {
                pending = runtime.PendingWatchStates;
                runtime.PendingWatchStates = null;
            }

            if (pending is null || WatchStateChanged is not { } subscribers)
            {
                return;
            }

            foreach (var change in pending)
            {
                foreach (var handler in subscribers.GetInvocationList())
                {
                    try
                    {
                        Deliver((Action<DriveWatchState>)handler, change);
                    }
                    catch (Exception exception)
                    {
                        // A state reporter that throws cannot report its own failure, and the state
                        // it was told about stays readable through Drives. Discarded through the
                        // variable rather than an empty body, which is this repository's idiom for a
                        // deliberate swallow and what keeps RCS1075 honest here.
                        _ = exception;
                    }
                }
            }
        }
        finally
        {
            runtime.DeliveryLock.Exit();
        }
    }

    /// <summary>Deliveries that found another thread delivering the same drive's changes and waited for it.</summary>
    long _contendedWatchStateDeliveries;

    /// <summary>A test seam: how many deliveries waited for another thread delivering the same drive.</summary>
    internal long ContendedWatchStateDeliveriesForTest => Interlocked.Read(ref _contendedWatchStateDeliveries);

    /// <summary>A test seam: whether the calling thread holds <see cref="_stateLock" />.</summary>
    internal bool IsStateLockHeldForTest => _stateLock.IsHeldByCurrentThread;
}
