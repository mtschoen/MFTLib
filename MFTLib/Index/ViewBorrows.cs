namespace MFTLib.Index;

/// <summary>One view's borrows across all its current and retired block handles.</summary>
sealed class ViewBorrows
{
    readonly Lock _gate = new();
    int _count;
    bool _closed;
    TaskCompletionSource? _drained;

    // Closing publishes with Volatile.Write under _gate; per-access handle reads acquire that flag.
    internal bool IsClosed => Volatile.Read(ref _closed);

    internal int OutstandingCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    internal bool TryTake()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }
            _count++;
            return true;
        }
    }

    internal void Return()
    {
        lock (_gate)
        {
            if (--_count == 0)
            {
                _drained?.TrySetResult();
            }
        }
    }

    internal Task CloseAndDrainAsync()
    {
        lock (_gate)
        {
            Volatile.Write(ref _closed, true);
            if (_count == 0)
            {
                return Task.CompletedTask;
            }
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }
}
