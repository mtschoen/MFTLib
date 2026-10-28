namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Raised once per applied change whose current or previous path lies inside this view, in journal order, to
    ///     every subscriber, even when an earlier subscriber threw for an earlier change (or for
    ///     this one). The mutation and the USN cursor are already applied to the mapped block by the time any handler
    ///     runs: the watch applies the whole batch and releases its root's
    ///     write gate before raising this event at all, so a throwing handler never undoes anything
    ///     and never stops another handler from seeing the rest of the batch. A close record that
    ///     repeats only reasons its open cycle already reported applies its metadata to the
    ///     block without raising this event, so within one block a transition raises one change even
    ///     though NTFS writes at least two journal records for it. A handler exception is reported
    ///     through <see cref="WatchFaulted" /> as <see cref="WatchFaultKind.Subscriber" />. A rescan
    ///     can publish before an old change is delivered, and its new watch can repeat that change
    ///     during catch-up. Queries can lag until that watch is CaughtUp.
    ///     Cross-view moves reach both views as Renamed, and each Entry belongs to its receiving view's root block.
    ///     A handler must not block on a lifecycle call of any index: see
    ///     <see cref="WatchFaulted" />.
    /// </summary>
    public event Action<FileChange>? Changed;

    /// <summary>
    ///     Raised for this view's first subscriber fault per watch instance, and for requesting views every time the shared watch
    ///     ends with a volume, apply, or channel fault, and after every scan of a volume whose
    ///     journal catch-up was lost or whose replacement watch could not start. Every fault names
    ///     its volume. Requesting recipients are captured when a scan fault settles; subscriber faults stay on their view.
    ///     Raised from that scan's pump, or for <see cref="WatchFaultKind.CatchUpLost" />
    ///     and <see cref="WatchFaultKind.RescanRestart" /> from the scan after publication drained
    ///     the old pump, so faults of different roots may be raised
    ///     concurrently while one root's faults never overlap. The scan operation raises its
    ///     fault while it still holds the root's lifecycle gate, so a handler must queue, not
    ///     wait for, a rescan or start of that root. Exceptions thrown by fault handlers are
    ///     discarded.
    ///     <para>
    ///         Inside any <see cref="Changed" />, <see cref="WatchFaulted" /> or
    ///         <see cref="WatchStateChanged" /> handler of any index,
    ///         whichever root it concerns, <c>StartWatchingAsync</c>, <c>StopWatchingAsync</c>,
    ///         <c>RescanAsync</c>, <c>DisposeAsync</c> and a
    ///         <c>WaitForCatchUpAsync</c> that has not yet settled fail at once with
    ///         <see cref="InvalidOperationException" />, because each can wait for a pump that is itself
    ///         blocked in a handler. The rejection also covers work the handler starts and that runs
    ///         before the handler returns. Queue such a call to run after the handler returns, for
    ///         example with <see cref="Task.Run(Action)" />. Queries, <see cref="Status" />, and a wait
    ///         that has already settled are allowed.
    ///     </para>
    /// </summary>
    public event Action<WatchFault>? WatchFaulted;

    /// <summary>
    ///     Raised on every attached view for each change of the scan's <see cref="RootWatchStatus.State" />, with
    ///     the root's <see cref="RootStatus.Watch" /> after the change, whose
    ///     <see cref="RootWatchStatus.StateVersion" /> is the change's version: a start,
    ///     a catch-up, a fault, a recovery being queued, finishing (the restarted watch reads
    ///     <see cref="WatchState.CatchingUp" />, then <see cref="WatchState.CaughtUp" />)
    ///     or failing, a rescan retiring and replacing the watch, a lost catch-up, a refused start,
    ///     a stop, and disposal. Every version of a root is delivered exactly once, after the change
    ///     is visible in <see cref="Status" />, by a thread that changed the root's state: a root's
    ///     pump, a scan or recovery holding the root's lifecycle gate, or the caller of a lifecycle
    ///     method, including <see cref="DisposeAsync" />, which delivers it before that method
    ///     returns. No state lock or write gate is held during delivery. A change a fault caused is
    ///     delivered before <see cref="WatchFaulted" /> reports that fault. Once
    ///     <see cref="DisposeAsync" /> has begun, <see cref="Status" /> throws
    ///     <see cref="ObjectDisposedException" />, so a handler of a disposal change reads the event
    ///     itself.
    ///     <para>
    ///         One root's changes are delivered one at a time, in version order; different roots'
    ///         changes can be delivered concurrently. A handler must therefore not block waiting for
    ///         another change of the same root. A consumer that also reads <see cref="Status" />,
    ///         for example to seed a subscription made after the index opened, applies an event only
    ///         when its <see cref="RootWatchStatus.StateVersion" /> is larger than the last one it applied
    ///         for that root, since its read can be newer than an event still being delivered. A
    ///         decision a consumer tagged with a version is superseded by any event of the same root
    ///         with a larger one, which is how it orders its own publication against a fault that
    ///         lands after it read the state.
    ///     </para>
    ///     <para>
    ///         The reentrancy rules of <see cref="WatchFaulted" /> apply: lifecycle calls of this
    ///         index fail at once inside a handler, which must queue them instead. Exceptions
    ///         thrown by handlers are discarded, and every subscriber still receives every change.
    ///     </para>
    /// </summary>
    public event Action<WatchStateChange>? WatchStateChanged;

    Action<FileChange>? ChangedSubscribers => Changed;
    Action<WatchFault>? WatchFaultedSubscribers => WatchFaulted;
    Action<WatchStateChange>? WatchStateChangedSubscribers => WatchStateChanged;
}
