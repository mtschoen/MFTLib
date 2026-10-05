using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     Lets a test that turned <see cref="BrokerDiagnostics" /> on put the process-wide diagnostics
///     state back, so the next test starts with diagnostics at their defaults.
/// </summary>
public static class BrokerDiagnosticsIsolation
{
    /// <summary>
    ///     Undoes <see cref="BrokerDiagnostics.Enable" />: restores the default role tag and the
    ///     environment-variable-controlled enablement, and discards the log writer after it drains
    ///     the lines already queued. Call <see cref="BrokerDiagnostics.FlushAsync" /> first to
    ///     read those lines from the log file.
    /// </summary>
    public static void Reset() => BrokerDiagnostics.ResetToDefaults();

    /// <summary>
    ///     Waits until every line already queued reaches the log file, so a test can read what a broker
    ///     under test logged. Forwards to <see cref="BrokerDiagnostics.FlushAsync" />.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the queued lines are written.</returns>
    public static Task FlushAsync(CancellationToken cancellationToken) => BrokerDiagnostics.FlushAsync(cancellationToken);

    /// <summary>
    ///     Queues one diagnostics line on <paramref name="channel" />, as a broker under test would.
    ///     Forwards to <see cref="BrokerDiagnostics.Log" />.
    /// </summary>
    /// <param name="channel">The channel tag of the line.</param>
    /// <param name="message">The line.</param>
    public static void Log(string channel, string message) => BrokerDiagnostics.Log(channel, message);
}
