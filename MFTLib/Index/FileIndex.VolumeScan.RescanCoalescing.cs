namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        // I10 Rescans: StateLock protects the pending slot and waiter count. Taking LifecycleGate
        // and marking Started detach the slot in one state-lock section; later calls queue a new run.
        RescanRequest? _pendingRescan;
        readonly HashSet<Task> _rescanDrains = [];

        sealed class RescanRequest : IDisposable
        {
            internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenSource Abandoned { get; } = new();
            internal Task CancellationWork { get; set; } = Task.CompletedTask;
            internal int Waiters { get; set; }
            internal bool ProductionFinished { get; set; }
            internal bool Finished { get; set; }

            public void Dispose() => Abandoned.Dispose();
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
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, view.DisposalToken);
            try
            {
                await AwaitQueuedAsync(request.Completion.Task, wait.Token).ConfigureAwait(false);
                wait.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                bool lastWaiter;
                lock (StateLock)
                {
                    request.Waiters--;
                    lastWaiter = request.Waiters == 0;
                    if (lastWaiter)
                    {
                        if (request.Finished)
                        {
                            request.Dispose();
                        }
                        else if (!request.ProductionFinished)
                        {
                            if (ReferenceEquals(_pendingRescan, request))
                            {
                                _pendingRescan = null;
                            }
                            // CancelAsync sets cancellation immediately but runs user callbacks asynchronously.
                            request.CancellationWork = request.Abandoned.CancelAsync();
                        }
                    }
                }
                if (lastWaiter)
                {
                    // The final caller owns cancellation cleanup. Other callers end their own waits
                    // independently; the final one observes canonical restoration and post-commit restart.
                    await request.Drained.Task.ConfigureAwait(false);
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
                            if (ReferenceEquals(_pendingRescan, request))
                            {
                                _pendingRescan = null;
                            }
                        }
                        await RescanWithGateHeldAsync(recovery: null, production.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        LifecycleGate.Release();
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            Task cancellation;
            lock (StateLock)
            {
                request.ProductionFinished = true;
                cancellation = request.CancellationWork;
            }
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            lock (StateLock)
            {
                request.Finished = true;
                if (ReferenceEquals(_pendingRescan, request))
                {
                    _pendingRescan = null;
                }
                if (request.Waiters == 0)
                {
                    request.Dispose();
                }
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
