namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One entry per configured drive, keyed by upper-case drive letter, created with the index
    ///     and kept until it is disposed. Every watch and rescan operation on a drive acts on that
    ///     drive's entry only. The dictionary itself is never modified after construction.
    /// </summary>
    readonly Dictionary<char, DriveRuntime> _driveRuntimes = [];

    /// <summary>
    ///     <see cref="WatchInstanceState.Starting" />: the source has been invoked and has not
    ///     returned a handle yet. <see cref="WatchInstanceState.Running" />: the handle is published
    ///     and the pump is reading it. <see cref="WatchInstanceState.Faulted" />: the pump ended with
    ///     a fault while the instance was current.
    /// </summary>
    enum WatchInstanceState
    {
        Starting,
        Running,
        Faulted,

        /// <summary>Stopped or superseded; teardown is in progress.</summary>
        Retiring,

        /// <summary>Teardown has finished.</summary>
        Drained
    }

    /// <summary>Where a drive stands in its automatic recovery (spec 2.6.5).</summary>
    enum RecoveryState
    {
        /// <summary>No recovery is queued, running, or waiting for its restarted watch.</summary>
        None,

        /// <summary>
        ///     A recovery is queued or running for the drive's faulted watch; the drive reads
        ///     <see cref="WatchCatchUpState.Recovering" />.
        /// </summary>
        Recovering,

        /// <summary>
        ///     A recovery restarted the drive's watch, which has not reached
        ///     <see cref="WatchCatchUpState.CaughtUp" /> yet. A fault of that watch now ends the
        ///     recovery with <see cref="WatchFaultKind.Recovery" /> instead of queuing another.
        /// </summary>
        RecoveredAwaitingCatchUp
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
    ///     the one raiser that holds a gate is a scan operation raising
    ///     <see cref="WatchFaultKind.CatchUpLost" /> between attempts, which holds the drive's
    ///     lifecycle gate.
    /// </remarks>
    sealed class DriveRuntime
    {
        public DriveRuntime(char driveLetter)
        {
            DriveLetter = driveLetter;
        }

        public char DriveLetter { get; }

        /// <summary>Serializes start and rescan for this drive. Never taken under the state lock.</summary>
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);

        /// <summary>
        ///     Serializes this drive's batch application against the commit of this drive's block.
        ///     Never taken under the state lock.
        /// </summary>
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        /// <summary>Scans of this drive in a row whose journal catch-up was lost.</summary>
        public int ConsecutiveLostCatchUps;

        /// <summary>
        ///     Set while a scan operation of a watched drive retries after a lost catch-up, which is
        ///     what makes the drive read <see cref="WatchCatchUpState.Recovering" />.
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
        ///     Set by a start and cleared by a stop or by disposal. A rescan restarts the drive's
        ///     watch only while it is set.
        /// </summary>
        public bool WatchRequested;

        public long NextGeneration;

        /// <summary>
        ///     The failure of the drive's last start when that start left no current instance: the
        ///     source threw, or the drive's block cannot be resumed. It is what makes the drive read
        ///     <see cref="WatchCatchUpState.Faulted" /> with no instance, and what a catch-up wait
        ///     then faults with. The start already threw it to its caller, so a stop never rethrows
        ///     it; the drive's next start, a stop, or a rescan that replaces the block clears it.
        /// </summary>
        public Exception? RefusedStartFault;
    }

    /// <summary>
    ///     One start of one drive's watch, with its own identity: the object reference, plus
    ///     <see cref="Generation" /> for diagnostics. Every effect its pump has is conditioned on
    ///     the instance still being its drive's <see cref="DriveRuntime.Current" />, so a retiring
    ///     pump never touches its successor's block or state.
    /// </summary>
    sealed class WatchInstance
    {
        readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _disposalLink;

        public WatchInstance(char driveLetter, long generation, DriveBlock armedBlock, CancellationToken disposalToken)
        {
            DriveLetter = driveLetter;
            Generation = generation;
            ArmedBlock = armedBlock;

            // Linked by hand rather than through CreateLinkedTokenSource, so the link can be
            // removed when the instance drains without disposing the source: a stop that decided
            // to cancel this instance under the state lock cancels it after releasing that lock,
            // and a disposed source would answer it with an exception.
            _disposalLink = disposalToken.UnsafeRegister(
                static state => (state as CancellationTokenSource)?.Cancel(), StartCancellation);
        }

        public char DriveLetter { get; }

        public long Generation { get; }

        /// <summary>The block the watch was started from. A batch applies only while it is still published.</summary>
        public DriveBlock ArmedBlock { get; }

        /// <summary>Cancels the source's start. Cancelled by the index's disposal as well.</summary>
        public CancellationTokenSource StartCancellation { get; } = new();

        /// <summary>Ends the pump's read, which is how a stop ends a running watch.</summary>
        public CancellationTokenSource PumpStop { get; } = new();

        public WatchInstanceState State = WatchInstanceState.Starting;

        public bool SubscriberFaultAnnounced;

        /// <summary>Rethrown once by the stop that retires this instance.</summary>
        public Exception? OutstandingFault;

        public WatchCatchUpSlot CatchUp { get; } = new();

        /// <summary>
        ///     Completes when teardown has finished: the source's start has returned, any handle
        ///     is disposed, and the pump has returned. It never faults.
        /// </summary>
        public Task Drained => _drained.Task;

        /// <summary>
        ///     Cancels the start and the pump. Neither source is ever disposed, so this is safe
        ///     from any thread at any point in the instance's life. Called outside the state lock,
        ///     because cancelling runs the source's callbacks.
        /// </summary>
        public void RequestStop()
        {
            StartCancellation.Cancel();
            PumpStop.Cancel();
        }

        public void CompleteDrain()
        {
            _disposalLink.Dispose();
            _drained.TrySetResult();
        }
    }

    /// <summary>A test seam: takes the drive's write gate, as a batch or a commit would.</summary>
    internal Task WaitForDriveWriteGateForTest(char driveLetter) => GetDriveRuntime(driveLetter).WriteGate.WaitAsync();

    /// <summary>A test seam: releases the write gate <see cref="WaitForDriveWriteGateForTest" /> took.</summary>
    internal void ReleaseDriveWriteGateForTest(char driveLetter) => GetDriveRuntime(driveLetter).WriteGate.Release();

    /// <summary>
    ///     A test seam: true when neither the drive's lifecycle gate nor its write gate is held.
    ///     Takes each without waiting and releases it again.
    /// </summary>
    internal bool AreDriveGatesFreeForTest(char driveLetter)
    {
        var runtime = GetDriveRuntime(driveLetter);
        var free = true;
        foreach (var gate in new[] { runtime.LifecycleGate, runtime.WriteGate })
        {
            if (gate.Wait(0))
            {
                gate.Release();
            }
            else
            {
                free = false;
            }
        }

        return free;
    }

    /// <summary>The drive's runtime, or <see cref="ArgumentException" /> for a letter not in this index.</summary>
    DriveRuntime GetDriveRuntime(char driveLetter)
    {
        return _driveRuntimes.TryGetValue(char.ToUpperInvariant(driveLetter), out var runtime)
            ? runtime
            : throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
    }

    /// <summary>
    ///     Clears the drive's recovery ticket, which a manual rescan, a consumer start, a stop, or
    ///     disposal supersedes. A recovery already past its revalidation keeps running, and its
    ///     failure is then no longer reported, since the ticket is not the drive's any more. The
    ///     caller holds <see cref="_stateLock" />.
    /// </summary>
    static void ClearRecoveryLocked(DriveRuntime runtime)
    {
        runtime.Recovery = null;
        runtime.RecoveryState = RecoveryState.None;
    }

    /// <summary>
    ///     Moves the drive's current instance to retiring, cancels its catch-up slot, and returns
    ///     it, or returns null when the drive has none. The caller holds <see cref="_stateLock" />
    ///     and calls <see cref="WatchInstance.RequestStop" /> on the result after releasing it.
    ///     An instance whose teardown already finished (a faulted one whose pump has returned) goes
    ///     straight to drained, since there is nothing left for a successor to wait for.
    /// </summary>
    static WatchInstance? RetireCurrentLocked(DriveRuntime runtime)
    {
        if (runtime.Current is not { } instance)
        {
            return null;
        }

        runtime.Current = null;
        instance.CatchUp.Cancel();
        if (instance.Drained.IsCompleted)
        {
            instance.State = WatchInstanceState.Drained;
            return instance;
        }

        instance.State = WatchInstanceState.Retiring;
        runtime.Retiring = instance;
        return instance;
    }

    /// <summary>
    ///     Finishes an instance's teardown. An instance that is still current keeps its state (a
    ///     faulted instance stays faulted and current until a stop, a start, or a rescan replaces
    ///     it); any other goes to drained and stops being its drive's retiring instance.
    /// </summary>
    void CompleteInstanceDrain(DriveRuntime runtime, WatchInstance instance)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(runtime.Current, instance))
            {
                instance.State = WatchInstanceState.Drained;
            }

            if (ReferenceEquals(runtime.Retiring, instance))
            {
                runtime.Retiring = null;
            }
        }

        instance.CompleteDrain();
    }

    /// <summary>
    ///     Awaits <paramref name="wait" />, cancelled by either token, without ever running the
    ///     awaiter's continuation inline on the stack that settles the wait or cancels a token.
    ///     The tokens reach the wait by cancelling a completion source created with
    ///     <see cref="TaskCreationOptions.RunContinuationsAsynchronously" /> from a registration,
    ///     not through <see cref="Task.WaitAsync(CancellationToken)" />, which makes no such
    ///     promise. A pump settling a waiter, or a handler cancelling a token, therefore never runs
    ///     the waiter's code on its own thread.
    /// </summary>
    static async Task AwaitQueuedAsync(Task wait, CancellationToken first, CancellationToken second)
    {
        if (wait.IsCompleted || (!first.CanBeCanceled && !second.CanBeCanceled))
        {
            await wait.ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstRegistration = first.Register(
            static (state, token) => (state as TaskCompletionSource)?.TrySetCanceled(token), completion);
        await using var secondRegistration = second.Register(
            static (state, token) => (state as TaskCompletionSource)?.TrySetCanceled(token), completion);
        _ = wait.ContinueWith(antecedent => CopyOutcome(antecedent, completion),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await completion.Task.ConfigureAwait(false);
    }

    static void CopyOutcome(Task antecedent, TaskCompletionSource completion)
    {
        if (antecedent.Exception is { } failure)
        {
            completion.TrySetException(failure.InnerExceptions);
        }
        else if (antecedent.IsCanceled)
        {
            completion.TrySetCanceled();
        }
        else
        {
            completion.TrySetResult();
        }
    }
}
