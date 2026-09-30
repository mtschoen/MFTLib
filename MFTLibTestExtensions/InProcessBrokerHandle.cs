using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     An in-process broker started by <see cref="BrokerTestHarness" />: the production
///     <see cref="BrokerProcess" /> a test drives, and the means to kill the host side of it.
/// </summary>
public sealed class InProcessBrokerHandle : IAsyncDisposable
{
    readonly Action _crash;

    internal InProcessBrokerHandle(BrokerProcess process, Action crash)
    {
        Process = process;
        _crash = crash;
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

    /// <summary>Disposes <see cref="Process" />, which ends the host's session and waits for it to return.</summary>
    public ValueTask DisposeAsync() => Process.DisposeAsync();
}
