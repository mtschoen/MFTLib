using System.Threading.Channels;

namespace MFTLib;

/// <summary>
///     The process's diagnostics sink: callers enqueue formatted lines without ever blocking, and
///     one background task appends them in order. A record that finds the buffer full is dropped
///     and counted, and so is a record whose append throws; before its next append the writer
///     first reports the count, so a gap in the log is always visible as a gap.
/// </summary>
internal sealed class BrokerDiagnosticsWriter
{
    /// <summary>Sized so a burst of frame traces from every drive fits while a slow disk catches up.</summary>
    public const int Capacity = 8192;

    // Set by the channel's drop callback, which runs synchronously inside the dropping TryWrite
    // on the caller's thread; TryWrite itself reports success for a dropped item.
    [ThreadStatic]
    static bool _droppedOnThisThread;

    readonly Channel<string> _channel;
    readonly Action<string> _appendLine;
    readonly Func<string> _roleProvider;
    readonly object _flushLock = new();
    readonly List<(long Target, TaskCompletionSource Completion)> _flushWaiters = [];
    long _dropped;
    long _accepted;
    long _processed;

    /// <summary>Starts the background drain that appends queued lines through <paramref name="appendLine" />.</summary>
    /// <param name="appendLine">Appends one complete line to the log; may block or throw.</param>
    /// <param name="roleProvider">
    ///     Supplies the role tag for the writer's own drop-report lines, read when the line is
    ///     written. Defaults to <c>client</c>.
    /// </param>
    public BrokerDiagnosticsWriter(Action<string> appendLine, Func<string>? roleProvider = null)
    {
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        }, _ => OnDropped());
        _appendLine = appendLine;
        _roleProvider = roleProvider ?? (() => "client");
        _ = Task.Factory.StartNew(DrainAsync, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Queues one line. Never blocks; a full buffer drops the line and counts it.</summary>
    public bool TryEnqueue(string line)
    {
        // Counted before the write so a flush that snapshots the count never misses a record
        // the drain can already see; a dropped record takes its count back in OnDropped. A
        // flush racing a drop can therefore wait on a record that never arrives until the next
        // record does, which only a full buffer can cause.
        Interlocked.Increment(ref _accepted);
        _droppedOnThisThread = false;
        _channel.Writer.TryWrite(line);
        return !_droppedOnThisThread;
    }

    void OnDropped()
    {
        _droppedOnThisThread = true;
        Interlocked.Decrement(ref _accepted);
        Interlocked.Increment(ref _dropped);
    }

    /// <summary>Completes once every record accepted before this call has been processed.</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        lock (_flushLock)
        {
            var target = Volatile.Read(ref _accepted);
            if (Volatile.Read(ref _processed) >= target)
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushWaiters.Add((target, completion));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Stops the drain after the queued records are processed.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    async Task DrainAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var line))
            {
                Append(line);
                NotifyProcessed();
            }
        }
    }

    void Append(string line)
    {
        var pending = Interlocked.Exchange(ref _dropped, 0);
        if (pending > 0)
        {
            try
            {
                _appendLine(
                    $"[{_roleProvider()}:{Environment.ProcessId}:diagnostics]  {pending} records dropped: buffer full");
            }
            catch (Exception exception)
            {
                // Best-effort only: diagnostics must never disturb the run. The report stays
                // owed and this record joins the count.
                _ = exception;
                Interlocked.Add(ref _dropped, pending + 1);
                return;
            }
        }

        try
        {
            _appendLine(line);
        }
        catch (Exception exception)
        {
            _ = exception;
            Interlocked.Increment(ref _dropped);
        }
    }

    void NotifyProcessed()
    {
        lock (_flushLock)
        {
            var processed = Interlocked.Increment(ref _processed);
            for (var index = _flushWaiters.Count - 1; index >= 0; index--)
            {
                if (_flushWaiters[index].Target <= processed)
                {
                    _flushWaiters[index].Completion.TrySetResult();
                    _flushWaiters.RemoveAt(index);
                }
            }
        }
    }
}
