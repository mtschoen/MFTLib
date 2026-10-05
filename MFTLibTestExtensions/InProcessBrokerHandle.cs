using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     An in-process broker started by <see cref="BrokerTestHarness" />: the production
///     <see cref="BrokerProcess" /> a test drives, and the means to kill the host side of it.
/// </summary>
public sealed class InProcessBrokerHandle : IAsyncDisposable
{
    readonly Action _crash;
    ScriptedBrokerResources? _resources;
    int _released;

    internal InProcessBrokerHandle(BrokerProcess process, Action crash)
    {
        Process = process;
        _crash = crash;
    }

    // Hands the handle the scan log and the resources it releases after the process is disposed.
    internal InProcessBrokerHandle Own(ScriptedBrokerResources resources)
    {
        _resources = resources;
        return this;
    }

    /// <summary>The client connected to the in-process host.</summary>
    public BrokerProcess Process { get; }

    /// <summary>
    ///     Simulates the broker process dying: every pipe end the host holds closes at once, without
    ///     the client's cooperation and without a graceful session end, so the client reads EOF on
    ///     the control pipe and on every drive channel, exactly as it does when an elevated broker
    ///     exits. The client's <see cref="BrokerProcess.Ended" /> then reports the loss, and every
    ///     pending or later request fails with <see cref="BrokerChannelLostException" />. Idempotent;
    ///     the handle still disposes normally afterwards.
    /// </summary>
    public void Crash() => _crash();

    /// <summary>
    ///     Every scan the host served so far, in the order it started, as the client requested it. Empty for a
    ///     handle MFTLib's own tests started with their own host.
    /// </summary>
    public IReadOnlyList<InProcessBrokerScan> Scans => _resources?.Scans() ?? [];

    readonly Lock _gate = new();
    Task? _disposeTask;

    /// <summary>
    ///     Disposes <see cref="Process" />, which ends the host's session and waits for it to return, then
    ///     releases the block sections the scans wrote into. Safe to call more than once.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposeTask = PerformDisposeAsync();
            }
            else if (_disposeTask.IsCompleted)
            {
                return ValueTask.CompletedTask;
            }

            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    async Task PerformDisposeAsync()
    {
        try
        {
            await Process.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _resources?.Dispose();
            }
        }
    }
}
