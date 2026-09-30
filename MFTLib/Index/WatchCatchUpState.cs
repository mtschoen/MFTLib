namespace MFTLib.Index;

/// <summary>
///     Where one drive's live watch stands in draining the journal backlog that was present when
///     the drive's current watch started. It is derived in this order: <see cref="Recovering" />
///     while the index rebuilds a watched drive's block; otherwise the current watch's own state;
///     otherwise <see cref="Faulted" /> when the drive's last start or restart failed or was
///     refused; otherwise <see cref="CatchingUp" /> while the watch is requested and its
///     replacement has not started yet (a rescan retired the old one); otherwise
///     <see cref="NotStarted" />.
/// </summary>
public enum WatchCatchUpState
{
    /// <summary>
    ///     The drive's watch is not requested and no start of it failed: it was never started, or
    ///     it has been stopped. Also the state of a drive that cannot be watched at all. A drive
    ///     whose start or automatic restart failed reads <see cref="Faulted" /> instead, even though
    ///     it has no watch running.
    /// </summary>
    NotStarted,

    /// <summary>
    ///     Entries through the journal tip captured at start have not all been applied yet. Also
    ///     read while a rescan has retired the drive's requested watch and not yet started its
    ///     replacement.
    /// </summary>
    CatchingUp,

    /// <summary>
    ///     The backlog present at start has been applied; every batch the drive applies now is a
    ///     live entry.
    /// </summary>
    CaughtUp,

    /// <summary>
    ///     The drive's watch is requested and the index is rebuilding its block: a recovery is
    ///     queued or running after a <see cref="WatchFaultKind.Drive" /> or
    ///     <see cref="WatchFaultKind.Apply" /> fault, or a scan is retrying after a lost journal
    ///     catch-up (<see cref="WatchFaultKind.CatchUpLost" />). The watch starts again from the
    ///     new block's cursor once a scan's catch-up holds.
    /// </summary>
    Recovering,

    /// <summary>
    ///     The drive's watch failed, its start or automatic restart failed, or its start was
    ///     refused, with detail in <see cref="DriveStatus.WatchFailureMessage" />. A failed
    ///     automatic recovery also leaves the drive here. Cleared by the drive's next start, by a
    ///     rescan that starts it again, or by a stop.
    /// </summary>
    Faulted
}

public sealed partial class FileIndex
{
    /// <summary>
    ///     One watch instance's catch-up. Every member is read and written under
    ///     <see cref="FileIndex._stateLock" />.
    /// </summary>
    sealed class WatchCatchUpSlot
    {
        /// <summary>The fault <see cref="MarkFaulted" /> recorded, which a cancellation reports instead.</summary>
        Exception? _fault;

        /// <summary>
        ///     <paramref name="waiter" /> is a wait issued while the drive's watch was requested
        ///     and no instance was current, which this instance's catch-up now settles.
        /// </summary>
        public WatchCatchUpSlot(TaskCompletionSource? waiter)
        {
            Waiter = waiter ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public WatchCatchUpState State { get; private set; } = WatchCatchUpState.CatchingUp;

        /// <summary>
        ///     Completes once per instance: with the catch-up, with the drive's watch failure, or
        ///     cancelled with the instance that owned it (stopped, superseded, or disposed). Its
        ///     continuations run asynchronously, so a pump settling it never runs a waiter's code
        ///     on its own stack.
        /// </summary>
        public TaskCompletionSource Waiter { get; private set; }

        public void Complete()
        {
            if (State != WatchCatchUpState.CatchingUp)
            {
                return;
            }

            State = WatchCatchUpState.CaughtUp;
            Waiter.TrySetResult();
        }

        /// <summary>
        ///     Cancels the waiter, unless the slot already faulted: a faulted slot's waiter reports
        ///     that fault, the outcome of the watch it followed, and whoever marked the fault
        ///     releases it once the drive's status reflects it.
        /// </summary>
        public void Cancel()
        {
            if (_fault is null)
            {
                Waiter.TrySetCanceled();
            }
        }

        /// <summary>Faults the slot and its waiter at once.</summary>
        public void Fault(Exception exception) => ReleaseFault(MarkFaulted(exception), exception);

        /// <summary>
        ///     Faults the slot without settling its waiter, and returns the waiter for the caller
        ///     to fault through <see cref="ReleaseFault" /> once the drive's status reflects
        ///     everything the fault leads to. A slot that already caught up gets a fresh waiter, so
        ///     a wait issued after the fault reports it rather than the earlier catch-up.
        /// </summary>
        public TaskCompletionSource MarkFaulted(Exception exception)
        {
            State = WatchCatchUpState.Faulted;
            _fault = exception;
            if (Waiter.Task.IsCompleted)
            {
                Waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return Waiter;
        }

        /// <summary>
        ///     Faults a waiter <see cref="MarkFaulted" /> returned. Safe with or without
        ///     <see cref="FileIndex._stateLock" /> held, since its continuations run asynchronously.
        /// </summary>
        public static void ReleaseFault(TaskCompletionSource waiter, Exception exception)
        {
            waiter.TrySetException(exception);

            // The slot owns the fault even when no caller ever waits on it.
            _ = waiter.Task.Exception;
        }
    }
}
