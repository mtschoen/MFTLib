namespace MFTLib;

/// <summary>
///     Visits every registered pipe once per <see cref="BrokerLiveness.HeartbeatInterval" /> on
///     one dedicated background thread, not the thread pool, so a scan that saturates the pool
///     cannot starve it. The interval comes from a timer on the host's <see cref="TimeProvider" />,
///     which signals the event the thread waits on, so a fake clock drives it. A visit only starts
///     writes (<see cref="HostPipeWriter.Visit" />); it never waits for one, so a pipe whose write is
///     blocked delays no other pipe's heartbeat.
/// </summary>
internal sealed class BrokerHeartbeatSender : IDisposable
{
    readonly TimeProvider _timeProvider;
    readonly Action? _visited;
    readonly ManualResetEventSlim _wake = new(false);
    readonly Lock _gate = new();
    readonly List<HostPipeWriter> _pipes = [];
    readonly Thread _thread;
    readonly ITimer _timer;
    volatile bool _stopping;

    /// <summary>Starts the sender thread and its interval timer; pipes join through <see cref="Register" />.</summary>
    /// <param name="timeProvider">The host's clock.</param>
    /// <param name="visited">Test hook: runs on the sender thread after each visit.</param>
    public BrokerHeartbeatSender(TimeProvider timeProvider, Action? visited)
    {
        _timeProvider = timeProvider;
        _visited = visited;
        _thread = new Thread(Run) { IsBackground = true, Name = "MFTLib broker heartbeat" };
        _thread.Start();
        _timer = timeProvider.CreateTimer(_ => _wake.Set(), null, BrokerLiveness.HeartbeatInterval,
            BrokerLiveness.HeartbeatInterval);
    }

    public void Register(HostPipeWriter pipe)
    {
        lock (_gate)
        {
            _pipes.Add(pipe);
        }
    }

    public void Unregister(HostPipeWriter pipe)
    {
        lock (_gate)
        {
            _pipes.Remove(pipe);
        }
    }

    void Run()
    {
        while (true)
        {
            // aislop-ignore-next-line csharp-sync-over-async -- a ManualResetEventSlim, not a Task: blocking this dedicated thread between visits is the design
            _wake.Wait();
            // Reset before visiting, so a tick that lands during the visit wakes the next one.
            _wake.Reset();
            if (_stopping)
            {
                return;
            }

            HostPipeWriter[] pipes;
            lock (_gate)
            {
                pipes = _pipes.ToArray();
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var pipe in pipes)
            {
                pipe.Visit(now);
            }

            _visited?.Invoke();
        }
    }

    /// <summary>
    ///     Stops the timer and the thread, and waits for a visit in progress to finish. The wake
    ///     event is left undisposed: a timer callback already in flight may still set it, and the
    ///     event holds no kernel handle because nothing reads its wait handle.
    /// </summary>
    public void Dispose()
    {
        _timer.Dispose();
        _stopping = true;
        _wake.Set();
        _thread.Join();
    }
}
