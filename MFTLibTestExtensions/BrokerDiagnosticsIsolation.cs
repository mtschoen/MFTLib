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
}
