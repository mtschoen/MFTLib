using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        Task? _teardown;

        public ValueTask DisposeAsync()
        {
            lock (StateLock)
            {
                MarkClosingLocked();
                _teardown ??= Task.Run(CloseAsync, CancellationToken.None);
                return new ValueTask(_teardown);
            }
        }

        // I12 Teardown: closing cancellation, watch/recovery drain, lifecycle then write gate,
        // retired and current mappings, canonical ownership, then Closed. No gate under StateLock.
        async Task CloseAsync()
        {
            ExceptionDispatchInfo? cancellationFailure = null;
            try
            {
                lock (StateLock)
                {
                    MarkClosingLocked();
                }
                try
                {
                    await CancelClosingAsync().ConfigureAwait(false);
                }
                catch (AggregateException exception)
                {
                    cancellationFailure = ExceptionDispatchInfo.Capture(exception);
                }
                WatchHandoff handoff;
                lock (StateLock)
                {
                    handoff = PrepareDisposalLocked();
                }
                RaiseWatchStateChanged();
                await handoff.DrainAsync().ConfigureAwait(false);
                await DrainRescansAsync().ConfigureAwait(false);
                await LifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                Hooks.LifecycleGatesTakenForDisposal?.Invoke();
                await WriteGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    foreach (var view in ViewsSnapshot())
                    {
                        await view.Borrows.CloseAndDrainAsync().ConfigureAwait(false);
                    }
                    await ReleaseRetiredBlocksAsync().ConfigureAwait(false);
                    RootBlock? current;
                    lock (StateLock)
                    {
                        current = DetachCurrentBlockLocked();
                    }
                    if (current is not null)
                    {
                        await current.ReleaseState.DrainAndReleaseAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    WriteGate.Release();
                    LifecycleGate.Release();
                }
                cancellationFailure?.Throw();
            }
            finally
            {
                lock (StateLock)
                {
                    ReleaseCanonicalOwnershipLocked();
                }
                _closed.TrySetResult();
            }
        }

        internal void RemoveView(IndexView view)
        {
            lock (StateLock)
            {
                _views.Remove(view);
            }
        }
    }
}
