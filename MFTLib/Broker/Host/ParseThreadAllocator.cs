namespace MFTLib;

/// <summary>
///     Divides one parse thread per processor among the host's running scans. At most
///     <c>processorCount</c> scans run at once, so every running scan has at least one thread;
///     later scans wait in arrival order. With <c>r</c> running scans each holds
///     <c>processorCount / r</c> threads, and the first <c>processorCount % r</c> in admission
///     order hold one more, so the allowances always sum to <c>processorCount</c>. Every
///     admission and every release recomputes the split under one lock and writes it into each
///     running scan's <see cref="ParseThreadAllowance" />, which the native parser reads at its
///     next chunk. Nothing computes a share anywhere else.
/// </summary>
internal sealed class ParseThreadAllocator
{
    readonly Lock _gate = new();
    readonly int _processorCount;
    readonly List<ParseThreadRegistration> _running = [];
    readonly LinkedList<TaskCompletionSource<ParseThreadRegistration>> _queue = new();

    public ParseThreadAllocator(int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processorCount, 1);
        _processorCount = processorCount;
    }

    public int RunningScanCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>Scans waiting for admission, in arrival order.</summary>
    internal int QueuedScanCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    ///     Admits the scan while fewer than <c>processorCount</c> scans run, otherwise queues it in
    ///     arrival order. A cancelled wait leaves the queue; a scan admitted in the same moment
    ///     its wait was cancelled still receives its registration and must dispose it.
    /// </summary>
    public ValueTask<ParseThreadRegistration> AdmitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource<ParseThreadRegistration>> node;
        lock (_gate)
        {
            if (_queue.Count == 0 && _running.Count < _processorCount)
            {
                return ValueTask.FromResult(AdmitLocked());
            }

            node = _queue.AddLast(
                new TaskCompletionSource<ParseThreadRegistration>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return new ValueTask<ParseThreadRegistration>(WaitForAdmissionAsync(node, cancellationToken));
    }

    async Task<ParseThreadRegistration> WaitForAdmissionAsync(
        LinkedListNode<TaskCompletionSource<ParseThreadRegistration>> node, CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(() => LeaveQueue(node, cancellationToken)).ConfigureAwait(false))
        {
            return await node.Value.Task.ConfigureAwait(false);
        }
    }

    void LeaveQueue(LinkedListNode<TaskCompletionSource<ParseThreadRegistration>> node,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // A node already taken off the queue was admitted; its registration stands.
            if (node.List == null)
            {
                return;
            }

            _queue.Remove(node);
        }

        node.Value.TrySetCanceled(cancellationToken);
    }

    internal void Release(ParseThreadRegistration registration)
    {
        var admitted = new List<(TaskCompletionSource<ParseThreadRegistration> Waiter, ParseThreadRegistration Registration)>();
        lock (_gate)
        {
            if (!_running.Remove(registration))
            {
                return;
            }

            while (_queue.First is { } next && _running.Count < _processorCount)
            {
                _queue.RemoveFirst();
                admitted.Add((next.Value, AdmitLocked()));
            }

            RebalanceLocked();
        }

        // Completed outside the lock: a waiter's continuation never runs while the split is held.
        foreach (var (waiter, admittedRegistration) in admitted)
        {
            waiter.SetResult(admittedRegistration);
        }
    }

    ParseThreadRegistration AdmitLocked()
    {
        var registration = new ParseThreadRegistration(this);
        _running.Add(registration);
        RebalanceLocked();
        return registration;
    }

    void RebalanceLocked()
    {
        var count = _running.Count;
        if (count == 0)
        {
            return;
        }

        var share = _processorCount / count;
        var remainder = _processorCount % count;
        for (var position = 0; position < count; position++)
        {
            _running[position].Allowance.Count = share + (position < remainder ? 1 : 0);
        }
    }
}

/// <summary>One running scan's place in the <see cref="ParseThreadAllocator" />.</summary>
internal sealed class ParseThreadRegistration : IDisposable
{
    readonly ParseThreadAllocator _allocator;
    int _disposed;

    internal ParseThreadRegistration(ParseThreadAllocator allocator)
    {
        _allocator = allocator;
    }

    /// <summary>Written by the allocator for as long as the scan runs.</summary>
    public ParseThreadAllowance Allowance { get; } = new(1);

    /// <summary>Ends the registration once, rebalances the remaining scans and admits queued ones.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocator.Release(this);
        }
    }
}
