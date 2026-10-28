using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class IndexView
    {
        /// <summary>
        ///     Noted changes not yet delivered through <see cref="FileIndex.WatchStateChanged" />, in
        ///     version order.
        /// </summary>
        List<WatchStateChange>? _pendingWatchStates;

        /// <summary>
        ///     Serializes delivery of this volume's <see cref="FileIndex.WatchStateChanged" />, held from
        ///     taking <see cref="_pendingWatchStates" /> until the last handler returns. Taken before
        ///     the state lock, never under it or under a write gate.
        /// </summary>
        Lock DeliveryLock { get; } = new();

        /// <summary>
        ///     Serializes delivery of this volume's <see cref="FileIndex.WatchFaulted" />, whichever thread
        ///     raises it. Taken with no other lock held.
        /// </summary>
        Lock FaultDeliveryLock { get; } = new();

        long _contendedWatchStateDeliveries;

        public long ContendedWatchStateDeliveries => Interlocked.Read(ref _contendedWatchStateDeliveries);

        internal void QueueWatchStateLocked(WatchStateChange change) => (_pendingWatchStates ??= []).Add(change);

        /// <summary>
        ///     Delivers the root's noted changes to every subscriber, in version order, isolating
        ///     each handler's failure. Called with no state lock or write gate held after every section
        ///     that notes a change, and by <see cref="RaiseWatchFaulted" /> before it reports a fault.
        ///     The root's <see cref="DeliveryLock" /> is held from taking the queue until
        ///     its last handler returns, so a caller that finds the queue empty returns only once
        ///     every change noted before it has been delivered: that is what keeps a fault's
        ///     <see cref="FileIndex.WatchFaulted" /> behind the state change it caused when another thread took
        ///     the queue first.
        /// </summary>
        public void RaiseWatchStateChanged()
        {
            if (!DeliveryLock.TryEnter())
            {
                Interlocked.Increment(ref _contendedWatchStateDeliveries);
                DeliveryLock.Enter();
            }

            try
            {
                List<WatchStateChange>? pending;
                lock (StateLock)
                {
                    pending = _pendingWatchStates;
                    _pendingWatchStates = null;
                }

                if (pending is null || Index.WatchStateChangedSubscribers is not { } subscribers)
                {
                    return;
                }

                foreach (var change in pending)
                {
                    foreach (var handler in subscribers.GetInvocationList())
                    {
                        try
                        {
                            FileIndex.Deliver((Action<WatchStateChange>)handler, change);
                        }
                        catch (Exception exception)
                        {
                            // A state reporter that throws cannot report its own failure, and the state
                            // it was told about stays readable through Roots. Discarded through the
                            // variable rather than an empty body, which is this repository's idiom for a
                            // deliberate swallow and what keeps RCS1075 honest here.
                            _ = exception;
                        }
                    }
                }
            }
            finally
            {
                DeliveryLock.Exit();
            }
        }

        internal void RaiseWatchFaulted(WatchFault fault)
        {
            RaiseWatchStateChanged();
            try
            {
                if (Index.WatchFaultedSubscribers is { } subscribers)
                {
                    lock (FaultDeliveryLock)
                    {
                        FileIndex.DeliverToEach(subscribers, fault);
                    }
                }
            }
            catch (Exception exception)
            {
                // A fault reporter that throws cannot safely report its own failure. The original
                // fault remains available through StopWatchingAsync. Discarded through the variable
                // rather than an empty body, which is this repository's idiom for a deliberate
                // swallow and what keeps RCS1075 honest here.
                _ = exception;
            }
        }

        public void RaiseChanged(IReadOnlyList<FileChange> changes)
        {
            var subscribers = Index.ChangedSubscribers;
            if (subscribers is null)
            {
                return;
            }

            List<Exception>? handlerExceptions = null;
            foreach (var change in changes)
            {
                foreach (var handler in subscribers.GetInvocationList())
                {
                    try
                    {
                        FileIndex.Deliver((Action<FileChange>)handler, change);
                    }
                    catch (Exception exception)
                    {
                        (handlerExceptions ??= []).Add(exception);
                    }
                }
            }

            if (handlerExceptions is not { Count: > 0 })
            {
                return;
            }

            if (handlerExceptions.Count == 1)
            {
                // Rethrowing the caught instance directly would overwrite its stack trace with this
                // throw site inside MFTLib, costing a consumer the frame in their own handler that
                // actually threw. Capturing preserves it.
                ExceptionDispatchInfo.Capture(handlerExceptions[0]).Throw();
            }

            throw new AggregateException(handlerExceptions);
        }
    }
}
