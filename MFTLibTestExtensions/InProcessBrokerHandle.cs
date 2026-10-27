using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     An in-process broker started by <see cref="BrokerTestHarness" />: the production client a test drives
///     through <see cref="BrokerTestHarness.CreateSession" />, and the means to kill the host side of it.
/// </summary>
public sealed class InProcessBrokerHandle : IAsyncDisposable
{
    readonly Action _crash;
    readonly ScriptedBrokerResources _resources;
    int _released;

    internal InProcessBrokerHandle(BrokerProcess process, Action crash,
        ScriptedBrokerResources resources)
    {
        Process = process;
        _crash = crash;
        _resources = resources;
    }

    /// <summary>The client connected to the in-process host.</summary>
    internal BrokerProcess Process { get; }

    /// <summary>Completes with the reason the client's session ended, when it does.</summary>
    public Task<string> Ended => Process.Ended;

    /// <summary>
    ///     Simulates the broker process dying: every pipe end the host holds closes at once, without
    ///     the client's cooperation and without a graceful session end, so the client reads EOF on
    ///     the control pipe and on every volume channel, exactly as it does when an elevated broker
    ///     exits. <see cref="Ended" /> then reports the loss, and every
    ///     pending or later request fails with an <see cref="IOException" />. Idempotent;
    ///     the handle still disposes normally afterwards.
    /// </summary>
    public void Crash() => _crash();

    /// <summary>
    ///     Every scan attempt the host began, in start order, recorded before querying its journal cursor.
    ///     Includes attempts whose cursor query or scan source subsequently fails.
    /// </summary>
    public IReadOnlyList<InProcessBrokerScan> Scans => _resources.Scans();

    readonly Lock _gate = new();
    Task? _disposeTask;

    /// <summary>
    ///     Disposes the client, which ends the host's session and waits for it to return, then
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
                _resources.Dispose();
            }
        }
    }
}
