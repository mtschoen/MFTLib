using System.Threading.Channels;

namespace MFTLib;

/// <summary>
///     The process's diagnostics sink: callers enqueue formatted lines without waiting for the log, and
///     one background task appends them in order. Queue saturation and append failures are counted
///     separately; before its next append the writer reports their counts and the latest append
///     exception's type and message, so a gap in the log names its cause.
/// </summary>
sealed class BrokerDiagnosticsWriter
{
    /// <summary>Sized so a burst of frame traces from every drive fits while a slow disk catches up.</summary>
    public const int Capacity = 8192;

    readonly Channel<string> _channel;
    readonly Action<string> _appendLine;
    readonly Func<string> _roleProvider;
    readonly Action? _completeSink;

    // Guards _accepted and the flush waiters. A record is queued and counted under it, so the
    // records counted are exactly the ones queued, in queue order, and a flush's snapshot never
    // includes a record that will not reach the drain.
    readonly Lock _flushLock = new();
    readonly List<(long Target, TaskCompletionSource Completion)> _flushWaiters = [];
    long _dropped;
    // Only the drain touches append failure state; retain the latest cause without an unbounded list.
    long _appendFailures;
    string? _lastAppendFailure;
    long _accepted;
    long _processed;

    /// <summary>
    ///     Starts the background drain for an owned <paramref name="sink" />, disposing it once the drain exits.
    /// </summary>
    /// <param name="sink">The file sink that appends lines and is disposed on drain completion.</param>
    /// <param name="roleProvider">
    ///     Supplies the role tag for the writer's own drop-report lines, read when the line is
    ///     written. Defaults to <c>client</c>.
    /// </param>
    public BrokerDiagnosticsWriter(BrokerDiagnosticsFileSink sink, Func<string>? roleProvider = null)
        : this(sink.AppendLine, roleProvider, sink.Dispose)
    {
    }

    /// <summary>
    ///     Starts the background drain that appends queued lines through <paramref name="appendLine" />
    ///     and finalizes any owned sink through <paramref name="completeSink" /> once the drain exits.
    /// </summary>
    /// <param name="appendLine">Appends one complete line to the log; may block or throw.</param>
    /// <param name="roleProvider">
    ///     Supplies the role tag for the writer's own drop-report lines, read when the line is
    ///     written. Defaults to <c>client</c>.
    /// </param>
    /// <param name="completeSink">
    ///     Optional finalizer invoked once on the drain after every accepted line has been processed.
    /// </param>
    public BrokerDiagnosticsWriter(Action<string> appendLine, Func<string>? roleProvider = null,
        Action? completeSink = null)
    {
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            // TryWrite on a full Wait-mode channel returns false instead of blocking.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _appendLine = appendLine;
        _roleProvider = roleProvider ?? (() => "client");
        _completeSink = completeSink;
        Completion = Task.Run(DrainAsync);
    }

    /// <summary>Completes once admission is closed, all accepted records are processed, and any owned sink is finalized.</summary>
    public Task Completion { get; }

    /// <summary>
    ///     Queues one line. Never waits for the drain; a full queue drops the line and counts it,
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

            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

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

    /// <summary>Closes admission without waiting; the drain finalizes any owned sink after processing accepted records.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    async Task DrainAsync()
    {
        try
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
        finally
        {
            try
            {
                _completeSink?.Invoke();
            }
            catch (Exception exception)
            {
                // Closing a best-effort diagnostic sink must not replace the broker's outcome.
                RecordAppendFailure(exception);
            }
        }
    }

    void Append(string line)
    {
        var pending = Interlocked.Exchange(ref _dropped, 0);
        if (pending > 0 || _appendFailures > 0)
        {
            try
            {
                _appendLine(
                    $"[{_roleProvider()}:{Environment.ProcessId}:diagnostics]  {pending + _appendFailures} records dropped: {DropReason(pending)}");
                _appendFailures = 0;
                _lastAppendFailure = null;
            }
            catch (Exception exception)
            {
                // Best-effort only: diagnostics must never disturb the run. The report stays
                // owed and this record joins the count.
                Interlocked.Add(ref _dropped, pending);
                RecordAppendFailure(exception);
                return;
            }
        }

        try
        {
            _appendLine(line);
        }
        catch (Exception exception)
        {
            RecordAppendFailure(exception);
        }
    }

    string DropReason(long queueDrops)
    {
        if (_appendFailures == 0)
        {
            return "queue full";
        }

        return queueDrops == 0
            ? $"append failed: {_lastAppendFailure}"
            : $"queue full ({queueDrops}); append failed ({_appendFailures}): {_lastAppendFailure}";
    }

    void RecordAppendFailure(Exception exception)
    {
        _appendFailures++;
        _lastAppendFailure = $"{exception.GetType().Name}: {exception.Message}";
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
