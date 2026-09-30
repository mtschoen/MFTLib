using System.Threading.Channels;

namespace MFTLib;

/// <summary>
///     The process's diagnostics sink: callers enqueue formatted lines without waiting for the log, and
///     one background task appends them in order. A record that finds the buffer full is dropped
///     and counted, and so is a record whose append throws; before its next append the writer
///     first reports the count, so a gap in the log is always visible as a gap.
/// </summary>
internal sealed class BrokerDiagnosticsWriter
{
    /// <summary>Sized so a burst of frame traces from every drive fits while a slow disk catches up.</summary>
    public const int Capacity = 8192;

    readonly Channel<string> _channel;
    readonly Action<string> _appendLine;
    readonly Func<string> _roleProvider;

    // Guards _accepted and the flush waiters. A record is queued and counted under it, so the
    // records counted are exactly the ones queued, in queue order, and a flush's snapshot never
    // includes a record that will not reach the drain.
    readonly Lock _flushLock = new();
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
            // TryWrite on a full Wait-mode channel returns false instead of blocking.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _appendLine = appendLine;
        _roleProvider = roleProvider ?? (() => "client");
        _ = Task.Run(DrainAsync);
    }

    /// <summary>
    ///     Queues one line. Never waits for the drain; a full buffer drops the line and counts it,
    ///     and a writer that was completed refuses it the same way.
    /// </summary>
    public bool TryEnqueue(string line)
    {
        lock (_flushLock)
        {
            if (_channel.Writer.TryWrite(line))
            {
                _accepted++;
                return true;
            }

            BeforeDropAccountedForTest?.Invoke();
            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

    /// <summary>Runs on the enqueuing thread, holding the lock a flush takes, as a record is dropped.</summary>
    internal Action? BeforeDropAccountedForTest { get; set; }

    /// <summary>Completes once every record accepted before this call has been processed.</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        lock (_flushLock)
        {
            var target = _accepted;
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
