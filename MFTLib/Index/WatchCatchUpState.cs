namespace MFTLib.Index;

/// <summary>
///     Where one drive's live watch stands in draining the journal backlog that was present when
///     the drive's current watch started. It describes the drive's current watch, so it reads
///     <see cref="NotStarted" /> whenever the drive has none.
/// </summary>
public enum WatchCatchUpState
{
    /// <summary>
    ///     The drive has no current watch: it was never started, or it has been stopped. Also the
    ///     state of a drive that cannot be watched at all.
    /// </summary>
    NotStarted,

    /// <summary>Entries through the journal tip captured at start have not all been applied yet.</summary>
    CatchingUp,

    /// <summary>
    ///     The backlog present at start has been applied; every batch the drive applies now is a
    ///     live entry.
    /// </summary>
    CaughtUp,

    /// <summary>
    ///     The drive's watch failed, or its start was refused, with detail in
    ///     <see cref="DriveStatus.WatchFailureMessage" />. Cleared by the drive's next start.
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
        public WatchCatchUpState State { get; private set; } = WatchCatchUpState.CatchingUp;

        /// <summary>
        ///     Completes once per instance: with the catch-up, with the drive's watch failure, or
        ///     cancelled with the instance that owned it (stopped, superseded, or disposed). Its
        ///     continuations run asynchronously, so a pump settling it never runs a waiter's code
        ///     on its own stack.
        /// </summary>
        public TaskCompletionSource Waiter { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete()
        {
            if (State != WatchCatchUpState.CatchingUp)
            {
                return;
            }

            State = WatchCatchUpState.CaughtUp;
            Waiter.TrySetResult();
        }

        public void Cancel() => Waiter.TrySetCanceled();

        /// <summary>
        ///     Faults the slot. A slot that already caught up gets a fresh, faulted waiter, so a
        ///     wait issued after the fault reports it rather than the earlier catch-up.
        /// </summary>
        public void Fault(Exception exception)
        {
            State = WatchCatchUpState.Faulted;
            if (!Waiter.TrySetException(exception))
            {
                var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                faulted.SetException(exception);
                Waiter = faulted;
            }

            // The slot owns the fault even when no caller ever waits on it.
            _ = Waiter.Task.Exception;
        }
    }
}
