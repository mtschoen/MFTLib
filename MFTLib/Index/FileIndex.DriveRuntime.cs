namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     <c>Starting</c>: the source has been invoked and has not
    ///     returned a handle yet. <c>Running</c>: the handle is published
    ///     and the pump is scheduled. <c>Faulted</c>: the pump ended with
    ///     a fault while the instance was current.
    /// </summary>
    enum WatchInstanceState
    {
        Starting,
        Running,
        Faulted,

        /// <summary>Stopped or superseded; teardown is in progress.</summary>
        Retiring
    }

    /// <summary>Where a drive stands in its automatic recovery (spec 2.6.5).</summary>
    enum RecoveryState
    {
        /// <summary>No recovery is queued, running, or waiting for its restarted watch.</summary>
        None,

        /// <summary>
        ///     A recovery is queued or running for the drive's faulted watch; the drive reads
        ///     <see cref="WatchState.Recovering" />.
        /// </summary>
        Recovering,

        /// <summary>
        ///     A recovery restarted the drive's watch, which has not reached
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
    ///     One drive's watch and scan records. The fields are read and written under
    ///     <see cref="FileIndex._stateLock" />; <see cref="LifecycleGate" /> and
    ///     <see cref="WriteGate" /> are not.
    /// </summary>
    /// <remarks>
    ///     Lock order, outermost first: the drive's <see cref="LifecycleGate" />, then its
    ///     <see cref="WriteGate" />, then <see cref="FileIndex._stateLock" />, which is a
    ///     <see cref="Lock" /> never held across an <c>await</c>. No code holds two drives'
    ///     lifecycle gates or two drives' write gates except disposal, which takes every lifecycle
    ///     gate and then every write gate in ascending drive-letter order. No code acquires a gate
    ///     while holding the state lock. <see cref="FileIndex.Changed" /> and
    ///     <see cref="FileIndex.WatchFaulted" /> are raised with no write gate and no state lock
    ///     held; a pump holds no gate at all when it raises, a recovery raises
    ///     <see cref="WatchFaultKind.Recovery" /> after releasing the drive's lifecycle gate, and
    ///     a scan operation raising <see cref="WatchFaultKind.CatchUpLost" /> between attempts or
    ///     <see cref="WatchFaultKind.RescanRestart" /> after a failed restart holds the drive's
    ///     lifecycle gate.
    /// </remarks>
    sealed partial class DriveRuntime
    {
        readonly FileIndex _index;

        public DriveRuntime(FileIndex index, IndexedDrive configuration)
        {
            _index = index;
            Configuration = configuration;
        }

        Lock StateLock => _index._stateLock;
        FileIndexOptions Options => _index._options;
        IndexHooks Hooks => _index.Hooks;
        CancellationToken DisposalToken => _index.DisposalToken;
        bool IsDisposed => _index._disposed;
        IIndexWatchSource? WatchSourceOrNull => Options.MftSource?.WatchSource;

        public IndexedDrive Configuration { get; }
        public DriveKey Drive => Configuration.Drive;
        public DriveBlock? Block;
        public PendingDriveResult? LastResult;
        public DriveState BlocklessState = DriveState.Offline;
        public DriveFailure? Failure;
        public string? WatchFailureMessage;
        public JournalCheckpointLoss? CheckpointLoss;
        public UnresumableCheckpointReason? UnresumableCheckpoint;
        public BlockOwnerLock? CanonicalLock;

        /// <summary>Serializes start and rescan for this drive. Never taken under the state lock.</summary>
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);

        /// <summary>
        ///     Serializes this drive's batch application against the commit of this drive's block.
        ///     Never taken under the state lock.
        /// </summary>
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        /// <summary>Scans of this drive in a row whose journal catch-up was lost.</summary>
        public int ConsecutiveLostCatchUps;

        /// <summary>Automatic catch-up retries exhausted; only a successful publication clears it.</summary>
        public bool RecoveryStopped;

        /// <summary>
        ///     Set while a scan operation of a watched drive retries after a lost catch-up, which is
        ///     what makes the drive read <see cref="WatchState.Recovering" />.
        /// </summary>
        public bool RetryingLostCatchUp;

        /// <summary>The lost catch-up a retrying scan operation is rescanning for, while it retries.</summary>
        public JournalCatchUpLostException? RetriedLostCatchUp;

        /// <summary>
        ///     The drive's queued or running recovery, or none. A manual rescan, a consumer start, a
        ///     stop, or disposal clears it; a recovery acts only while it is still this ticket.
        /// </summary>
        public RecoveryTicket? Recovery;

        /// <summary>Where the drive stands in its automatic recovery.</summary>
        public RecoveryState RecoveryState;

        /// <summary>The drive's current watch instance, or none.</summary>
        public WatchInstance? Current;

        /// <summary>The previous instance until its <see cref="WatchInstance.Drained" /> completes.</summary>
        public WatchInstance? Retiring;

        /// <summary>
        ///     An outstanding fault from the watch a manual rescan retired. Kept through teardown
        ///     and restart until stop takes it or the replacement start supersedes it.
        /// </summary>
        public Exception? RescanHandoffFault;

        /// <summary>
        ///     Set by a start, including one whose source threw or that was refused over an
        ///     unresumable block, and cleared by a stop, by disposal, or by a start its own caller
        ///     cancelled. A rescan restarts the drive's watch only while it is set.
        /// </summary>
        public bool WatchRequested;

        /// <summary>
        ///     A catch-up wait issued while the watch is requested and no instance is current, the
        ///     window between a rescan retiring the old watch and registering its replacement. The
        ///     replacement instance adopts it as its own catch-up waiter; a refusal or lost catch-up
        ///     faults it, and a stop or disposal cancels it.
        /// </summary>
        public TaskCompletionSource? RestartPendingWaiter;

        public long NextGeneration;

        /// <summary>
        ///     The failure of the drive's last start when that start left no current instance: the
        ///     source threw, or the drive's block cannot be resumed. It is what makes the drive read
        ///     <see cref="WatchState.Faulted" /> with no instance, and what a catch-up wait
        ///     then faults with. The start already threw it to its caller, so a stop never rethrows
        ///     it; the drive's next start, a stop, or a rescan that replaces the block clears it.
        /// </summary>
        public Exception? RefusedStartFault;

        /// <summary>
        ///     The <see cref="WatchState" /> last noted for the drive, which
        ///     <see cref="NoteWatchStateLocked" /> compares the derived state against.
        /// </summary>
        public WatchState NotedWatchState;

        /// <summary>The drive's <see cref="DriveWatchStatus.StateVersion" />.</summary>
        public long WatchStateVersion;

        /// <summary>
        ///     Noted changes not yet delivered through <see cref="FileIndex.WatchStateChanged" />, in
        ///     version order.
        /// </summary>
        public List<WatchStateChange>? PendingWatchStates;

        /// <summary>
        ///     Serializes delivery of this drive's <see cref="FileIndex.WatchStateChanged" />, held from
        ///     taking <see cref="PendingWatchStates" /> until the last handler returns. Taken before
        ///     the state lock, never under it or under a write gate.
        /// </summary>
        public Lock DeliveryLock { get; } = new();

        /// <summary>
        ///     Serializes delivery of this drive's <see cref="FileIndex.WatchFaulted" />, whichever thread
        ///     raises it. Taken with no other lock held.
        /// </summary>
        public Lock FaultDeliveryLock { get; } = new();
    }
}
