namespace MFTLib;

/// <summary>
///     Opt-in broker diagnostics. When environment variable
///     <c>MFTLIB_BROKER_DIAG=1</c> is set (or <see cref="Enable" /> has been called),
///     <see cref="Log" /> appends a timestamped, role-tagged line to
///     <c>{LogDirectory}/broker-diagnostics.log</c>; otherwise it is a no-op. Consumers
///     point <see cref="LogDirectory" /> at their own app-data directory before enabling
///     diagnostics; it defaults to the OS temp directory. The elevated child receives
///     diagnostics via the <c>--diag</c> flag (see <see cref="Enable" />) rather than the
///     env var, because a <c>runas</c> launch does not reliably inherit the parent's
///     environment. Used to reveal, on an on-hardware run, whether the cold scan used the
///     broker or fell back to a direct scan, what cursor the broker captured vs.
///     persisted, and - via <see cref="LogFrame" /> - the kind and byte length of every
///     frame on the wire, which is what localizes a pipe desync.
///     While diagnostics are enabled, the broker drops journal entries for the diagnostics
///     log files (its own and the client's, whose path arrives as <c>--diag-log</c>) before
///     building JournalBatch frames, matching by file reference number so a renamed log stays
///     filtered. Without that filter each logged frame would produce a USN record for the log
///     file, which would ship as another JournalBatch and be logged again - a self-sustaining
///     loop that drowns the real watch traffic. Set <c>MFTLIB_BROKER_DIAG_INCLUDE_SELF=1</c>
///     before spawning the client (forwarded as <c>--diag-include-self</c>) to keep those
///     entries when debugging the diagnostics themselves.
/// </summary>
public static class BrokerDiagnostics
{
    static bool _forced;

    // Tag distinguishing the caller process from the elevated broker child in the
    // shared log file. The broker child sets it to "broker" via Enable.
    static string _role = "client";

    /// <summary>
    ///     Directory the diagnostics log is written into. Consumers should set this to
    ///     their own app-data directory before enabling diagnostics. Defaults to the OS
    ///     temp directory.
    /// </summary>
    public static string LogDirectory { get; set; } = Path.GetTempPath();

    internal const string LogFileName = "broker-diagnostics.log";

    /// <summary>
    ///     Full path of this process's diagnostics log file. The client forwards it to the
    ///     elevated child as <c>--diag-log</c> so the broker can filter the log file's own
    ///     journal entries out of the watch stream.
    /// </summary>
    internal static string LogPath
    {
        get
        {
            var combined = Path.Combine(LogDirectory, LogFileName);
            if (OperatingSystem.IsWindows())
            {
                return Path.GetFullPath(combined);
            }

            // On non-Windows platforms (e.g. Linux unit tests / probes), preserve Windows
            // drive or extended roots (e.g. "C:\..." or "\\?\C:\...") without prepending
            // the Unix working directory. If relative, resolve against the current directory.
            if (BrokerDiagnosticsLogFilter.TryGetDriveLetter(combined, out _))
            {
                return combined;
            }

            return Path.GetFullPath(combined);
        }
    }

    /// <summary>
    ///     The client process's log path, forwarded with <c>--diag-log</c> so the broker can
    ///     drop the client log's journal entries too. Null when the client sent no path.
    /// </summary>
    internal static string? ClientLogPath { get; set; }

    static bool _includeSelfEntries;

    /// <summary>
    ///     Set by <c>--diag-include-self</c> (or <c>MFTLIB_BROKER_DIAG_INCLUDE_SELF=1</c>): ship
    ///     the diagnostics logs' own journal entries instead of dropping them. For debugging
    ///     the diagnostics themselves.
    /// </summary>
    internal static bool IncludeSelfEntries
    {
        get => _includeSelfEntries || Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF") == "1";
        set => _includeSelfEntries = value;
    }

    internal static bool Enabled =>
        _forced || Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG") == "1";

    /// <summary>
    ///     Force diagnostics on for this process and tag its log lines with
    ///     <paramref name="role" />. Used by the elevated broker child, which cannot rely on
    ///     inheriting the <c>MFTLIB_BROKER_DIAG</c> env var across the <c>runas</c> launch.
    /// </summary>
    public static void Enable(string role)
    {
        _forced = true;
        _role = role;
    }

    /// <summary>
    ///     Build the filter that drops both diagnostics log files' journal entries from
    ///     JournalBatch frames, or null when diagnostics are off (nothing is logged, so
    ///     nothing needs filtering) or <see cref="IncludeSelfEntries" /> opted the entries
    ///     back in.
    /// </summary>
    internal static BrokerDiagnosticsLogFilter? CreateLogFilter()
    {
        return Enabled && !IncludeSelfEntries
            ? new BrokerDiagnosticsLogFilter(LogPath, ClientLogPath)
            : null;
    }

    // Test seam: undo Enable() and restore the default role tag. Enable() has no
    // production counterpart that turns diagnostics back off (an elevated broker
    // child that calls it is short-lived), so only tests need this.
    internal static void ResetToDefaults()
    {
        _forced = false;
        _role = "client";
        ClientLogPath = null;
        _includeSelfEntries = false;
    }

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var path = LogPath;
            File.AppendAllText(path,
                $"{DateTime.UtcNow:O}  [{_role}:{Environment.ProcessId}]  {message}{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            // Best-effort only: diagnostics must never disturb the run. Swallowing is
            // intentional and scoped to this opt-in logging path.
            _ = exception;
        }
    }

    /// <summary>
    ///     Trace one frame on the wire: its kind byte and total length (kind byte + payload).
    ///     A read that records an unexpected kind, preceded by a frame whose length does not
    ///     match its real content, pinpoints which writer desynced the stream.
    /// </summary>
    public static void LogFrame(string direction, byte kind, int length)
    {
        Log($"frame {direction} kind={kind} len={length} t={Environment.CurrentManagedThreadId}");
    }
}
