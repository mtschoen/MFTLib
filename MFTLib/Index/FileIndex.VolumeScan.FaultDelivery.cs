namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        // I8 Faults: settlement captures the requesters and their outstanding copies under StateLock.
        // Delivery consumes only that captured set outside the lock, even if requests change meanwhile.
        IndexView[] CaptureFaultLocked(WatchFault fault)
        {
            lock (StateLock)
            {
                var recipients = _requesters.ToArray();
                foreach (var view in recipients)
                {
                    RecordViewFaultLocked(view, Current, fault.Exception, fault.Kind == WatchFaultKind.RescanRestart);
                }
                return recipients;
            }
        }

        static void RecordViewFaultLocked(IndexView view, WatchInstance? instance, Exception exception, bool replace = false)
        {
            if (replace || view.OutstandingFault is null ||
                (instance is not null && instance.Generation != view.OutstandingFaultGeneration))
            {
                view.OutstandingFault = exception;
                view.OutstandingFaultGeneration = instance?.Generation;
            }
        }

        /// <summary>
        ///     Notifies every requesting view that a journal change could not be stored on <paramref name="skip" />'s
        ///     lineage, once per lineage, with its cause as the exception. Under <see cref="StateLock" /> it claims the
        ///     lineage's notification and captures the recipients only while <paramref name="instance" /> is still the
        ///     root's running watch and the skip's lineage is still the published one, so a rescan that published a
        ///     replacement meanwhile suppresses it. Not a watch fault: no view records it as outstanding, so a stop
        ///     never returns it, and no watch state changes. Called by the pump with no gate or lock held; delivery
        ///     runs outside the lock.
        /// </summary>
        void RaiseCapacityExhausted(WatchInstance instance, CapacityExhaustion skip)
        {
            IndexView[] recipients;
            lock (StateLock)
            {
                if (IsClosing || !ReferenceEquals(Current, instance) || instance.State != WatchInstanceState.Running ||
                    !ReferenceEquals(Block?.Mapped, skip.Lineage) || !skip.Lineage.Capacity.TryClaimNotification())
                {
                    return;
                }

                recipients = [.. _requesters];
            }

            RaiseWatchFaulted(new WatchFault(WatchFaultKind.CapacityExhausted, Volume, skip.Cause), recipients);
        }

        static void RaiseWatchFaulted(WatchFault fault, IReadOnlyList<IndexView> recipients)
        {
            foreach (var view in recipients)
            {
                view.RaiseWatchFaulted(fault);
            }
        }
    }
}
