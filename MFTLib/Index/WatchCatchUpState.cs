namespace MFTLib.Index;

/// <summary>
///     Where one drive's live watch stands in draining the journal backlog that was present when
///     the drive's current arm started. The state is session-scoped: it exists only while a watch
///     session is draining the drive.
/// </summary>
public enum WatchCatchUpState
{
    /// <summary>
    ///     No watch session is draining this drive: the watch was never started, or it has been
    ///     stopped. Also the state of a drive that cannot be watched at all.
    /// </summary>
    NotStarted,

    /// <summary>Entries through the journal tip captured at arm time have not all been applied yet.</summary>
    CatchingUp,

    /// <summary>
    ///     The backlog present at arm time has been applied; every batch the drive applies now is
    ///     a live entry.
    /// </summary>
    CaughtUp,

    /// <summary>
    ///     The drive's watch failed, with detail in <see cref="DriveStatus.WatchFailureMessage" />.
    ///     Cleared the same way that message is: by the next arm.
    /// </summary>
    Faulted
}

public sealed partial class FileIndex
{
    sealed class WatchCatchUpSlot
    {
        readonly object _callbacksLock = new();
        List<Action<Task>>? _faultWaiterContinuations = [];

        public WatchCatchUpState State { get; set; }

        public Exception? Exception { get; set; }

        public bool WasCanceled { get; private set; }

        /// <summary>
        ///     Completes exactly once per arm: with the catch-up, with the drive's watch failure,
        ///     or cancelled with the arm that owned it (superseded, stopped, or disposed).
        /// </summary>
        public TaskCompletionSource Waiter { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Signals when this slot transitions to faulted, even if its <see cref="Waiter" /> had
        ///     already completed with success from an earlier catch-up.
        /// </summary>
        public TaskCompletionSource FaultWaiter { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => State switch
        {
            WatchCatchUpState.Faulted when Exception is not null => Task.FromException(Exception),
            _ => Waiter.Task
        };

        public void Cancel()
        {
            WasCanceled = true;
            Waiter.TrySetCanceled();
            FaultWaiter.TrySetCanceled();
            InvokeFaultWaiterContinuations(FaultWaiter.Task);
        }

        public void Fault(Exception exception)
        {
            State = WatchCatchUpState.Faulted;
            Exception = exception;
            FaultWaiter.TrySetException(exception);
            if (!Waiter.TrySetException(exception))
            {
                var faultedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                faultedSource.SetException(exception);
                Waiter = faultedSource;
            }

            InvokeFaultWaiterContinuations(FaultWaiter.Task);
        }

        public IDisposable RegisterFaultWaiter(Action<Task> continuation)
        {
            lock (_callbacksLock)
            {
                if (FaultWaiter.Task.IsCompleted)
                {
                    continuation(FaultWaiter.Task);
                    return EmptyDisposable.Instance;
                }

                _faultWaiterContinuations ??= [];
                _faultWaiterContinuations.Add(continuation);
                return new FaultWaiterRegistration(this, continuation);
            }
        }

        void InvokeFaultWaiterContinuations(Task task)
        {
            Action<Task>[] continuations;
            lock (_callbacksLock)
            {
                if (_faultWaiterContinuations is null || _faultWaiterContinuations.Count == 0)
                {
                    return;
                }

                continuations = [.. _faultWaiterContinuations];
                _faultWaiterContinuations = null;
            }

            foreach (var continuation in continuations)
            {
                continuation(task);
            }
        }

        sealed class FaultWaiterRegistration : IDisposable
        {
            readonly WatchCatchUpSlot _slot;
            Action<Task>? _continuation;

            public FaultWaiterRegistration(WatchCatchUpSlot slot, Action<Task> continuation)
            {
                _slot = slot;
                _continuation = continuation;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _continuation, null) is { } continuation)
                {
                    lock (_slot._callbacksLock)
                    {
                        _slot._faultWaiterContinuations?.Remove(continuation);
                    }
                }
            }
        }

        sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }

    sealed class CatchUpCoordinator
    {
        readonly TaskCompletionSource _completionSource;
        readonly IReadOnlyList<WatchCatchUpSlot> _slots;
        readonly List<IDisposable> _registrations = [];
        readonly object _gate = new();
        int _remainingCount;

        public CatchUpCoordinator(IReadOnlyList<WatchCatchUpSlot> slots, TaskCompletionSource completionSource)
        {
            _slots = slots;
            _remainingCount = slots.Count;
            _completionSource = completionSource;
        }

        public void Attach()
        {
            lock (_gate)
            {
                foreach (var slot in _slots)
                {
                    if (slot.State == WatchCatchUpState.Faulted && slot.Exception is not null)
                    {
                        SignalFaultedLocked(slot.Exception);
                        return;
                    }

                    if (slot.FaultWaiter.Task.IsFaulted)
                    {
                        SignalFaultedLocked(slot.FaultWaiter.Task.Exception);
                        return;
                    }

                    if (slot.WasCanceled || slot.FaultWaiter.Task.IsCanceled || slot.Task.IsCanceled)
                    {
                        SignalCanceledLocked();
                        return;
                    }
                }

                foreach (var slot in _slots)
                {
                    _registrations.Add(slot.RegisterFaultWaiter(task =>
                    {
                        if (task.IsFaulted)
                        {
                            SignalFaulted(task.Exception);
                        }
                        else if (task.IsCanceled)
                        {
                            SignalCanceled();
                        }
                    }));

                    if (slot.State == WatchCatchUpState.CaughtUp && slot.Task.IsCompletedSuccessfully)
                    {
                        SignalCompletedLocked();
                    }
                    else
                    {
                        slot.Task.ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                            {
                                SignalFaulted(task.Exception);
                            }
                            else if (task.IsCanceled)
                            {
                                SignalCanceled();
                            }
                            else
                            {
                                SignalCompleted();
                            }
                        }, TaskScheduler.Default);
                    }
                }
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                SignalCanceledLocked();
            }
        }

        void SignalCompleted()
        {
            lock (_gate)
            {
                SignalCompletedLocked();
            }
        }

        void SignalCompletedLocked()
        {
            if (_completionSource.Task.IsCompleted)
            {
                return;
            }

            foreach (var slot in _slots)
            {
                if (slot.State == WatchCatchUpState.Faulted && slot.Exception is not null)
                {
                    SignalFaultedLocked(slot.Exception);
                    return;
                }

                if (slot.FaultWaiter.Task.IsFaulted)
                {
                    SignalFaultedLocked(slot.FaultWaiter.Task.Exception);
                    return;
                }

                if (slot.WasCanceled || slot.FaultWaiter.Task.IsCanceled || slot.Task.IsCanceled)
                {
                    SignalCanceledLocked();
                    return;
                }
            }

            _remainingCount--;
            if (_remainingCount <= 0)
            {
                _completionSource.TrySetResult();
                DisposeRegistrationsLocked();
            }
        }

        void SignalFaulted(Exception? exception)
        {
            lock (_gate)
            {
                SignalFaultedLocked(exception);
            }
        }

        void SignalFaultedLocked(Exception? exception)
        {
            if (_completionSource.Task.IsCompleted)
            {
                return;
            }

            if (exception is AggregateException aggregateException)
            {
                _completionSource.TrySetException(aggregateException.InnerExceptions);
            }
            else if (exception is not null)
            {
                _completionSource.TrySetException(exception);
            }
            else
            {
                _completionSource.TrySetException(new InvalidOperationException("A watched drive faulted during catch-up."));
            }

            DisposeRegistrationsLocked();
        }

        void SignalCanceled()
        {
            lock (_gate)
            {
                SignalCanceledLocked();
            }
        }

        void SignalCanceledLocked()
        {
            if (_completionSource.Task.IsCompleted)
            {
                return;
            }

            _completionSource.TrySetCanceled();
            DisposeRegistrationsLocked();
        }

        void DisposeRegistrationsLocked()
        {
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }

            _registrations.Clear();
        }
    }
}
