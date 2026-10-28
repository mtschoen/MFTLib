namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class IndexView
    {
        // I9 Delivery: admission and closure share this lock. Closure rejects later handlers;
        // the final admitted delivery completes the asynchronous drain before disposal returns.
        readonly Lock _deliveryGate = new();
        bool _deliveriesClosed;
        int _deliveries;
        TaskCompletionSource? _deliveriesDrained;

        bool TryEnterDelivery()
        {
            lock (_deliveryGate)
            {
                if (_deliveriesClosed)
                {
                    return false;
                }
                _deliveries++;
                return true;
            }
        }

        void ExitDelivery()
        {
            lock (_deliveryGate)
            {
                if (--_deliveries == 0)
                {
                    _deliveriesDrained?.TrySetResult();
                }
            }
        }

        internal Task CloseDeliveriesAsync()
        {
            lock (_deliveryGate)
            {
                _deliveriesClosed = true;
                if (_deliveries == 0)
                {
                    return Task.CompletedTask;
                }
                _deliveriesDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _deliveriesDrained.Task;
            }
        }

        void InDelivery(Action action)
        {
            if (!TryEnterDelivery())
            {
                return;
            }
            try
            {
                action();
            }
            finally
            {
                ExitDelivery();
            }
        }
    }
}
