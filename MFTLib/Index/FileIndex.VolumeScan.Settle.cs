namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        // I11 Settles: one run at a time, selected under StateLock. Writes is frozen after
        // taking LifecycleGate, so joined cache-only readers never change an active production.
        SettleRun? _settle;
        bool _settleWrites = true;

        sealed class SettleRun
        {
            internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> WarmVerdict { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Writes { get; set; }
            internal RootFailureKind Decline { get; set; } = RootFailureKind.CacheDeclined;
        }

        internal async Task<MftUnavailableReason?> SettleViewAsync(IndexView view, CancellationToken cancellationToken)
        {
            await AttachToCurrentBlockAsync(view, cancellationToken).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, view.DisposalToken);
            while (true)
            {
                SettleRun run;
                lock (StateLock)
                {
                    if (Block is not null && _settle is null)
                    {
                        return null;
                    }
                    if (_settle is null)
                    {
                        _settle = new SettleRun();
                        var started = _settle;
                        _ = Task.Run(() => RunSettleAsync(started), CancellationToken.None);
                    }
                    run = _settle;
                }

                if (view.Options.CacheMode == CacheMode.ExistingOnly)
                {
                    var warm = await run.WarmVerdict.Task.WaitAsync(wait.Token).ConfigureAwait(false);
                    if (!run.Writes)
                    {
                        await run.Completion.Task.WaitAsync(wait.Token).ConfigureAwait(false);
                    }
                    else if (!warm)
                    {
                        lock (StateLock)
                        {
                            var result = new PendingRootResult
                            {
                                ProducerFailureMessage = run.Decline == RootFailureKind.InUse
                                    ? $"Root {view.Root}: cache block is in use by another FileIndex and CacheMode.ExistingOnly forbids a scan."
                                    : $"Root {view.Root}: no usable cache (missing, corrupt, or incompatible) and CacheMode.ExistingOnly forbids a scan."
                            };
                            view.OpenFailure = (new RootFailure(run.Decline, result.ProducerFailureMessage), result);
                        }
                    }
                    return null;
                }

                await run.Completion.Task.WaitAsync(wait.Token).ConfigureAwait(false);
                lock (StateLock)
                {
                    if (view.Options.Producer == ProducerSelection.Automatic && Block is null &&
                        _failure is { Kind: RootFailureKind.MftUnavailable })
                    {
                        return _lastMftFailure;
                    }
                    if (run.Writes || Block is not null || BlocklessState == RootState.Offline)
                    {
                        return null;
                    }
                }
            }
        }

        async Task RunSettleAsync(SettleRun run)
        {
            try
            {
                await PreviousClosed.WaitAsync(ClosingToken).ConfigureAwait(false);
                await LifecycleGate.WaitAsync(ClosingToken).ConfigureAwait(false);
                try
                {
                    lock (StateLock)
                    {
                        run.Writes = _views.Any(view => view.Options.CacheMode != CacheMode.ExistingOnly);
                        _settleWrites = run.Writes;
                    }
                    await AddRootAsync(ClosingToken).ConfigureAwait(false);
                }
                finally
                {
                    LifecycleGate.Release();
                }
                lock (StateLock)
                {
                    run.WarmVerdict.TrySetResult(Block is not null);
                    _settle = null;
                    run.Completion.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                lock (StateLock)
                {
                    _settle = null;
                    run.WarmVerdict.TrySetException(exception);
                    run.Completion.TrySetException(exception);
                }
            }
        }

        void DeclineWarmStart(RootFailureKind kind)
        {
            lock (StateLock)
            {
                if (_settle is { } run)
                {
                    run.Decline = kind;
                    run.WarmVerdict.TrySetResult(false);
                }
            }
        }

        internal async Task AttachToCurrentBlockAsync(IndexView view, CancellationToken cancellationToken)
        {
            await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RootBlock? current;
                lock (StateLock)
                {
                    current = Block;
                }
                var bound = current is null ? null : view.BindBlock(current.Mapped);
                lock (StateLock)
                {
                    _views.Add(view);
                    view.Block = bound;
                }
            }
            finally
            {
                WriteGate.Release();
            }
        }
    }
}
