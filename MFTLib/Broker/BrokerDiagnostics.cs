namespace MFTLib;

/// <summary>
/// Opt-in broker diagnostics. When environment variable
/// <c>MFTLIB_BROKER_DIAG=1</c> is set (or <see cref="Enable"/> has been called),
/// <see cref="Log"/> appends a timestamped, role-tagged line to
/// <c>{LogDirectory}/broker-diagnostics.log</c>; otherwise it is a no-op. Consumers
/// point <see cref="LogDirectory"/> at their own app-data directory before enabling
/// diagnostics; it defaults to the OS temp directory. The elevated child receives
/// diagnostics via the <c>--diag</c> flag (see <see cref="Enable"/>) rather than the
/// env var, because a <c>runas</c> launch does not reliably inherit the parent's
/// environment. Used to reveal, on an on-hardware run, whether the cold scan used the
/// broker or fell back to a direct scan, what cursor the broker captured vs.
/// persisted, and - via <see cref="LogFrame"/> - the kind and byte length of every
/// frame on the wire, which is what localizes a pipe desync.
/// </summary>
public static class BrokerDiagnostics
{
    static bool _forced;

    // Tag distinguishing the caller process from the elevated broker child in the
    // shared log file. The broker child sets it to "broker" via Enable.
    static string _role = "client";

    /// <summary>
    /// Directory the diagnostics log is written into. Consumers should set this to
    /// their own app-data directory before enabling diagnostics. Defaults to the OS
    /// temp directory.
    /// </summary>
    public static string LogDirectory { get; set; } = Path.GetTempPath();

    static bool Enabled =>
        _forced || Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG") == "1";

    /// <summary>
    /// Force diagnostics on for this process and tag its log lines with
    /// <paramref name="role"/>. Used by the elevated broker child, which cannot rely on
    /// inheriting the <c>MFTLIB_BROKER_DIAG</c> env var across the <c>runas</c> launch.
    /// </summary>
    public static void Enable(string role)
    {
        _forced = true;
        _role = role;
    }

    // Test seam: undo Enable() and restore the default role tag. Enable() has no
    // production counterpart that turns diagnostics back off (an elevated broker
    // child that calls it is short-lived), so only tests need this.
    internal static void ResetToDefaults()
    {
        _forced = false;
        _role = "client";
    }

    public static void Log(string message)
    {
        if (!Enabled)
            return;

        try
        {
            var path = Path.Combine(LogDirectory, "broker-diagnostics.log");
            File.AppendAllText(path, $"{DateTime.UtcNow:O}  [{_role}:{Environment.ProcessId}]  {message}{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            // Best-effort only: diagnostics must never disturb the run. Swallowing is
            // intentional and scoped to this opt-in logging path.
            _ = exception;
        }
    }

    /// <summary>
    /// Trace one frame on the wire: its kind byte and total length (kind byte + payload).
    /// A read that records an unexpected kind, preceded by a frame whose length does not
    /// match its real content, pinpoints which writer desynced the stream.
    /// </summary>
    public static void LogFrame(string direction, byte kind, int length) =>
        Log($"frame {direction} kind={kind} len={length} t={Environment.CurrentManagedThreadId}");
}
