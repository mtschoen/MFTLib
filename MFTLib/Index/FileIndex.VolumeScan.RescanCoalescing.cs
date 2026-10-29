namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        // I10 Rescans: StateLock protects the pending slot and waiter count. Taking LifecycleGate
        // and marking Started detach the slot in one state-lock section; later calls queue a new run.
        RescanRequest? _pendingRescan;
        RescanRequest? _runningRescan;
        readonly HashSet<Task> _rescanDrains = [];

        sealed class RescanRequest : IDisposable
        {
            internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenSource Abandoned { get; } = new();
            internal int Waiters { get; set; }
            internal int CleanupWaiters { get; set; }
            internal bool IsAbandoned { get; set; }
            internal bool ProductionFinished { get; set; }
            readonly List<(CancellationToken Token, IndexView View)> _callers = [];

            /// <summary>Records an admitted caller. The caller holds the scan's state lock.</summary>
            internal void AddCallerLocked(IndexView view, CancellationToken token) => _callers.Add((token, view));

            /// <summary>
            ///     Whether no caller still wants the result, read from each caller's own token and its
            ///     view's disposed flag rather than from cancellation callbacks, which can run late. The
            ///     caller holds the scan's state lock, under which views are marked disposed.
            /// </summary>
            internal bool IsAbandonedByEveryCallerLocked() => IsAbandoned ||
                _callers.TrueForAll(caller => caller.Token.IsCancellationRequested || caller.View.IsDisposed);

            public void Dispose() => Abandoned.Dispose();
        }

        sealed class RescanWaiter(VolumeScan scan, RescanRequest request)
        {
            int _released;

            internal void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    scan.ReleaseRescanWaiter(request);
                }
            }
        }

        void ReleaseRescanWaiter(RescanRequest request)
        {
            var cancelAbandoned = false;
            lock (StateLock)
            {
                request.Waiters--;
                if (request.Waiters == 0 && !request.ProductionFinished)
                {
                    if (ReferenceEquals(_pendingRescan, request))
                    {
                        _pendingRescan = null;
                    }
                    request.IsAbandoned = true;
                    cancelAbandoned = true;
                }
            }
            if (cancelAbandoned)
            {
                request.Abandoned.Cancel();
            }
        }

        internal async Task RescanAsync(IndexView view, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RescanRequest request;
            lock (StateLock)
            {
                ThrowIfClosing(cancellationToken);
                if (view.IsDisposed)
                {
                    throw new OperationCanceledException("The view is being disposed.", view.DisposalToken);
                }
                if (_pendingRescan is null)
                {
                    _pendingRescan = new RescanRequest();
                    _rescanDrains.Add(_pendingRescan.Drained.Task);
                    var created = _pendingRescan;
                    _ = Task.Run(() => RunRescanAsync(created), CancellationToken.None);
                }
                request = _pendingRescan;
                request.Waiters++;
                request.CleanupWaiters++;
                request.AddCallerLocked(view, cancellationToken);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, view.DisposalToken);
            var waiter = new RescanWaiter(this, request);
            try
            {
                using var registration = wait.Token.Register(waiter.Release);
                await AwaitQueuedAsync(request.Completion.Task, wait.Token).ConfigureAwait(false);
                wait.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                waiter.Release();
                bool lastCleanup;
                bool waitForDrain;
                lock (StateLock)
                {
                    request.CleanupWaiters--;
                    lastCleanup = request.CleanupWaiters == 0;
                    waitForDrain = lastCleanup || request.IsAbandoned;
                }
                if (waitForDrain)
                {
                    // The final caller owns cancellation cleanup. Other callers end their own waits
                    // independently; the final one observes canonical restoration and post-commit restart.
                    await request.Drained.Task.ConfigureAwait(false);
                }
                if (lastCleanup)
                {
                    request.Dispose();
                }
            }
        }

        async Task RunRescanAsync(RescanRequest request)
        {
            Exception? failure = null;
            using (var production = CancellationTokenSource.CreateLinkedTokenSource(ClosingToken, request.Abandoned.Token))
            {
                try
                {
                    await LifecycleGate.WaitAsync(production.Token).ConfigureAwait(false);
                    try
                    {
                        lock (StateLock)
                        {
                            production.Token.ThrowIfCancellationRequested();
                            _runningRescan = request;
                            if (ReferenceEquals(_pendingRescan, request))
                            {
                                _pendingRescan = null;
                            }
                        }
                        await RescanWithGateHeldAsync(recovery: null, production.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (StateLock)
                        {
                            _runningRescan = null;
                        }
                        LifecycleGate.Release();
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            lock (StateLock)
            {
                request.ProductionFinished = true;
                if (failure is null)
                {
                    request.Completion.TrySetResult();
                }
                else
                {
                    request.Completion.TrySetException(failure);
                }
                _rescanDrains.Remove(request.Drained.Task);
                request.Drained.TrySetResult();
            }
        }

        Task DrainRescansAsync()
        {
            lock (StateLock)
            {
                return Task.WhenAll(_rescanDrains);
            }
        }
    }
}
