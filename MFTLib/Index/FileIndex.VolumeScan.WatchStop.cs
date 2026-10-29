using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>Withdraws this root's request and drains its watch without taking the lifecycle gate.</summary>
        public async Task StopRootWatchingAsync(IndexView view, CancellationToken cancellationToken)
        {
            if (FileIndex.RejectInsideHandler(nameof(StopWatchingAsync)) is { } rejection)
            {
                throw rejection;
            }

            ObjectDisposedException.ThrowIf(IsClosing, typeof(FileIndex));
            WatchInstance? retired;
            Task? previousDrain;
            Task? drain;
            Exception? outstandingFault;
            lock (StateLock)
            {
                if (!IsWatchingLocked(view))
                {
                    throw new InvalidOperationException(
                        $"Root {Root} is not watching, so there is no watch to stop.");
                }

                view.Request = null;
                _requesters.Remove(view);
                outstandingFault = TakeOutstandingFaultLocked(view);
                if (AnyRequestLocked)
                {
                    view.WithdrawalDrain = null;
                    if (outstandingFault is not null)
                    {
                        ExceptionDispatchInfo.Capture(outstandingFault).Throw();
                    }
                    return;
                }
                AbandonConsumerStartLocked();
                _refusedStartFault = null;
                CancelRestartPendingWaiterLocked();
                ClearRecoveryLocked();
                previousDrain = _retiring?.Drained;
                retired = RetireCurrentLocked();
                drain = retired?.Drained is { } retiredDrain && previousDrain is not null
                    ? Task.WhenAll(retiredDrain, previousDrain)
                    : retired?.Drained ?? previousDrain;
                view.WithdrawalDrain = drain;
                NoteWatchStateLocked();
            }

            RaiseWatchStateChanged();
            retired?.RequestStop();
            if (drain is not null)
            {
                await AwaitQueuedAsync(drain, cancellationToken).ConfigureAwait(false);
            }

            lock (StateLock)
            {
                // A fault recorded for a request admitted during the drain belongs to that request.
                if (view.Request is null)
                {
                    outstandingFault ??= TakeOutstandingFaultLocked(view);
                }
            }

            if (outstandingFault is not null)
            {
                ExceptionDispatchInfo.Capture(outstandingFault).Throw();
            }
        }

        static Exception? TakeOutstandingFaultLocked(IndexView view)
        {
            var fault = view.OutstandingFault;
            view.OutstandingFault = null;
            view.OutstandingFaultGeneration = null;
            return fault;
        }

        void ClearRequestsLocked()
        {
            foreach (var view in _requesters)
            {
                view.Request = null;
            }
            _requesters.Clear();
        }

        /// <summary>
        ///     Moves the root's current instance to retiring, cancels its catch-up slot, and returns
        ///     it, or returns null when the root has none. The caller holds <see cref="StateLock" />
        ///     and calls <see cref="WatchInstance.RequestStop" /> on the result after releasing it.
        ///     An instance whose teardown already finished (a faulted one whose pump has returned) goes
        ///     straight out, since there is nothing left for a successor to wait for.
        /// </summary>
        WatchInstance? RetireCurrentLocked()
        {
            if (Current is not { } instance)
            {
                return null;
            }

            Current = null;
            instance.CatchUp.Cancel();
            if (instance.Drained.IsCompleted)
            {
                return instance;
            }

            instance.State = WatchInstanceState.Retiring;
            _retiring = instance;
            return instance;
        }

        /// <summary>
        ///     Finishes an instance's teardown. An instance that is still current keeps its state (a
        ///     faulted instance stays faulted and current until a stop, a start, or a rescan replaces
        ///     it). The retiring instance is detached when its teardown completes.
        /// </summary>
        void CompleteInstanceDrain(WatchInstance instance)
        {
            lock (StateLock)
            {
                if (ReferenceEquals(_retiring, instance))
                {
                    _retiring = null;
                    NoteWatchStateLocked();
                }
            }

            instance.CompleteDrain();
            RaiseWatchStateChanged();
        }
    }
}
