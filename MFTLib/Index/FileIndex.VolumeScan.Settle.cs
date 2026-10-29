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
            internal bool RequestedWrites { get; set; }
            internal bool ModeSelected { get; set; }
            internal RootFailureKind Decline { get; set; } = RootFailureKind.CacheDeclined;
        }

        internal async Task<MftUnavailableReason?> SettleViewAsync(IndexView view, CancellationToken cancellationToken)
        {
            await AttachToCurrentBlockAsync(view, cancellationToken).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, view.DisposalToken);
            while (true)
            {
                SettleRun run;
                SettleRun? started = null;
                lock (StateLock)
                {
                    if (Block is not null && _settle is null)
                    {
                        return null;
                    }
                    if (Block is null && _manualRescanRunning && view.Options.CacheMode == CacheMode.ExistingOnly)
                    {
                        RecordViewDeclineLocked(view, RootFailureKind.CacheDeclined);
                        return null;
                    }
                    if (_settle is null)
                    {
                        started = new SettleRun();
                        _settle = started;
                    }
                    run = _settle;
                    if (!run.ModeSelected && view.Options.CacheMode != CacheMode.ExistingOnly)
                    {
                        run.RequestedWrites = true;
                    }
                }
                if (started is not null)
                {
                    _ = RunSettleAsync(started);
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
                            RecordViewDeclineLocked(view, run.Decline);
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
                    bool needsSettle;
                    lock (StateLock)
                    {
                        run.Writes = run.RequestedWrites;
                        run.ModeSelected = true;
                        _settleWrites = run.Writes;
                        needsSettle = Block is null;
                    }
                    if (needsSettle)
                    {
                        await AddRootAsync(ClosingToken).ConfigureAwait(false);
                    }
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

        static void RecordViewDeclineLocked(IndexView view, RootFailureKind kind)
        {
            var result = new PendingRootResult
            {
                ProducerFailureMessage = kind == RootFailureKind.InUse
                    ? $"Root {view.Root}: cache block is in use by another FileIndex and CacheMode.ExistingOnly forbids a scan."
                    : $"Root {view.Root}: no usable cache (missing, corrupt, or incompatible) and CacheMode.ExistingOnly forbids a scan."
            };
            view.OpenFailure = (new RootFailure(kind, result.ProducerFailureMessage), result);
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
