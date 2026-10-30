namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     <c>Starting</c>: the source has been invoked and has not
    ///     returned a handle yet. <c>Running</c>: the handle is published
    ///     and the pump is scheduled. <c>Faulted</c>: the pump ended with
    ///     a fault while the instance was current.
    /// </summary>
    internal enum WatchInstanceState
    {
        Starting,
        Running,
        Faulted,

        /// <summary>Stopped or superseded; teardown is in progress.</summary>
        Retiring
    }

    /// <summary>Where a root stands in its automatic recovery (spec 2.6.5).</summary>
    enum RecoveryState
    {
        /// <summary>No recovery is queued, running, or waiting for its restarted watch.</summary>
        None,

        /// <summary>
        ///     A recovery is queued or running for the root's faulted watch; the root reads
        ///     <see cref="WatchState.Recovering" />.
        /// </summary>
        Recovering,

        /// <summary>
        ///     A recovery restarted the root's watch, which has not reached
        ///     <see cref="WatchState.CaughtUp" /> yet. A fault of that watch now ends the
        ///     recovery with <see cref="WatchFaultKind.Recovery" /> instead of queuing another.
        /// </summary>
        RecoveredAwaitingCatchUp
    }

    /// <summary>Why a published block's journal cursor cannot be resumed by a watch.</summary>
    enum UnresumableCheckpointReason
    {
        /// <summary>A cache-only open adopted the block despite a lost checkpoint.</summary>
        CacheOnlyAdoption,

        /// <summary>The scan that produced the block lost its journal catch-up.</summary>
        LostCatchUp
    }

    /// <summary>
    ///     One scan's shared watch and production records. The fields are read and written under
    ///     <see cref="StateLock" />; <see cref="LifecycleGate" /> and
    ///     <see cref="WriteGate" /> are not.
    /// </summary>
    /// <remarks>
    ///     Lock order, outermost first: the scan's <see cref="LifecycleGate" />, then its
    ///     <see cref="WriteGate" />, then <see cref="StateLock" />, which is a
    ///     <see cref="Lock" /> never held across an <c>await</c>. A disposal takes gates only on
    ///     its own scan, and only when its last view detaches. No code acquires a gate
    ///     while holding the state lock. <see cref="FileIndex.Changed" /> and
    ///     <see cref="FileIndex.WatchFaulted" /> are raised with no write gate and no state lock
    ///     held; a pump holds no gate at all when it raises, a recovery raises
    ///     <see cref="WatchFaultKind.Recovery" /> after releasing the root's lifecycle gate, and
    ///     a scan operation raising <see cref="WatchFaultKind.CatchUpLost" /> between attempts or
    ///     <see cref="WatchFaultKind.RescanRestart" /> after a failed restart holds the root's
    ///     lifecycle gate.
    /// </remarks>
    internal sealed partial class VolumeScan(VolumeScanCreation creation, VolumeScanKey? key = null, Task? previousClosed = null) : IAsyncDisposable
    {
        internal VolumeScanKey? Key { get; } = key;
        internal Task PreviousClosed { get; } = previousClosed ?? Task.CompletedTask;
        readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Closed => _closed.Task;
        internal Lock StateLock { get; } = new();
        FileIndexOptions Options => creation.Options;
        IndexHooks Hooks => creation.Hooks;
        // Like the gates, this source stays readable by operations racing completed teardown.
        readonly CancellationTokenSource _closing = new();
        internal CancellationToken ClosingToken => _closing.Token;
        bool _isClosing;
        internal bool IsClosing => Volatile.Read(ref _isClosing);
        internal void MarkClosingLocked() => Volatile.Write(ref _isClosing, true);
        IIndexWatchSource? WatchSourceOrNull => Options.MftSource?.WatchSource;

        /// <summary>
        ///     The registered scan's volume root, or a private scan's own root. Automatic fallback
        ///     detaches the view and creates a private walk scan; this owner's resolution is immutable.
        /// </summary>
        ResolvedRoot Resolved { get; } = creation.Resolved;

        public RootBlock? Block;
        PendingRootResult? _lastResult;
        public RootState BlocklessState = RootState.Offline;
        RootFailure? _failure;
        string? _watchFailureMessage;
        JournalCheckpointLoss? _checkpointLoss;
        UnresumableCheckpointReason? _unresumableCheckpoint;
        BlockOwnerLock? _canonicalLock;

        /// <summary>Serializes start and rescan for this volume. Never taken under the state lock.</summary>
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);

        /// <summary>
        ///     Serializes this volume's batch application against the commit of this volume's block.
        ///     Never taken under the state lock.
        /// </summary>
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        /// <summary>
        ///     The root's queued or running recovery, or none. A manual rescan, a consumer start, a
        ///     stop, or disposal clears it; a recovery acts only while it is still this ticket.
        /// </summary>
        public RecoveryTicket? Recovery;

        /// <summary>Where the root stands in its automatic recovery.</summary>
        RecoveryState _recoveryState;

        /// <summary>The root's current watch instance, or none.</summary>
        public WatchInstance? Current;

        /// <summary>The previous instance until its <see cref="WatchInstance.Drained" /> completes.</summary>
        WatchInstance? _retiring;

        /// <summary>
        ///     A catch-up wait issued while the watch is requested and no instance is current, the
        ///     window between a rescan retiring the old watch and registering its replacement. The
        ///     replacement instance adopts it as its own catch-up waiter; a refusal or lost catch-up
        ///     faults it, and a stop or disposal cancels it.
        /// </summary>
        TaskCompletionSource? _restartPendingWaiter;

        long _nextGeneration;

        /// <summary>
        ///     The failure of the root's last start when that start left no current instance: the
        ///     source threw, or the root's block cannot be resumed. It is what makes the root read
        ///     <see cref="WatchState.Faulted" /> with no instance, and what a catch-up wait
        ///     then faults with. The start already threw it to its caller, so a stop never rethrows
        ///     it; the root's next start, a stop, or a rescan that replaces the block clears it.
        /// </summary>
        Exception? _refusedStartFault;
    }
}
