using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     Lets a test that turned <see cref="BrokerDiagnostics" /> on put the process-wide diagnostics
///     state back, so the next test starts with diagnostics at their defaults.
/// </summary>
public static class BrokerDiagnosticsIsolation
{
    /// <summary>
    ///     Restores the default role, OS temp log directory and environment-controlled enablement,
    ///     and completes and detaches the current writer. Stop logging producers and await
    ///     <see cref="FlushAsync" /> before resetting diagnostics or deleting their log directory.
    /// </summary>
    public static void Reset() => BrokerDiagnostics.Dependencies.ResetToDefaults();

    /// <summary>
    ///     Waits until every line already queued has been appended or dropped, so a test can read what a broker
    ///     under test logged in <c>broker-diagnostics.&lt;role&gt;.log</c>. Use <see cref="BrokerDiagnosticsIsolation.Log" />
    ///     to queue a test line. Drop notices name queue saturation or the latest append failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the queued lines have been processed.</returns>
    public static Task FlushAsync(CancellationToken cancellationToken) => BrokerDiagnostics.FlushAsync(cancellationToken);

    /// <summary>
    ///     Queues one diagnostics line on <paramref name="channel" />, as a broker under test would.
    ///     Forwards to the broker's diagnostics log.
    /// </summary>
    /// <param name="channel">The channel tag of the line.</param>
    /// <param name="message">The line.</param>
    public static void Log(string channel, string message) => BrokerDiagnostics.Log(channel, message);
}
