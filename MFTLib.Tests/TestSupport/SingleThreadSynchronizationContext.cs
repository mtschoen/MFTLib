using System.Collections.Concurrent;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A synchronization context shaped like a desktop UI thread: every posted callback queues for one dedicated
///     thread, which runs them in order. Work posted while a callback runs waits for that callback to return.
/// </summary>
sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    readonly Thread _thread;

    public SingleThreadSynchronizationContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = nameof(SingleThreadSynchronizationContext) };
        _thread.Start();
    }

    /// <summary>The managed id of the one thread that runs every posted callback.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(HostChannelHarness.HangGuard);
        _queue.Dispose();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("A test posts to this context; nothing may block on it.");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="action" /> on the context's thread with the context installed.</summary>
    public Task<T> RunAsync<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                result.SetResult(action());
            }
            catch (Exception exception)
            {
                result.SetException(exception);
            }
        }, null);
        return result.Task;
    }

    void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}

/// <summary>A task scheduler that runs nothing until the test calls <see cref="RunPending" /> on its own thread.</summary>
sealed class QueuedTaskScheduler : TaskScheduler
{
    readonly ConcurrentQueue<Task> _tasks = new();

    /// <summary>Runs every queued task inline and returns how many ran.</summary>
    public int RunPending()
    {
        var count = 0;
        while (_tasks.TryDequeue(out var task))
        {
            TryExecuteTask(task);
            count++;
        }

        return count;
    }

    protected override void QueueTask(Task task) => _tasks.Enqueue(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
}
