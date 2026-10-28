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

        static void RaiseWatchFaulted(WatchFault fault, IReadOnlyList<IndexView> recipients)
        {
            foreach (var view in recipients)
            {
                view.RaiseWatchFaulted(fault);
            }
        }
    }
}
