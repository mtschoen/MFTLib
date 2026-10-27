namespace MFTLib;

/// <summary>
///     Opt-in broker diagnostics. When environment variable
///     <c>MFTLIB_BROKER_DIAG=1</c> is set (or <see cref="Enable" /> has been called),
///     client activity appends timestamped, role-tagged lines to
///     <c>broker-diagnostics.&lt;role&gt;.log</c> in the configured directory; otherwise it is a no-op.
///     Consumers pass their own app-data directory to <see cref="Enable" /> after early
///     broker dispatch; environment-only opt-in defaults to the OS temp directory.
///     The elevated child receives diagnostics via <c>--diag</c> and the absolute
///     <c>--diag-log</c> path rather than the
///     env var, because a <c>runas</c> launch does not reliably inherit the parent's
///     environment. Child dispatch installs a broker-owned sink that exclusively creates
///     <c>broker-diagnostics.broker.log</c> and retains the open file through its drain.
///     Used to reveal, on an on-hardware run, what cursor the broker captured vs.
///     persisted, and the kind and byte length of every
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
    // Each role owns a separate log file. The broker child sets its role to "broker" via EnableBroker.
    const string DefaultRole = "client";

    const string LogFilePrefix = "broker-diagnostics.";
    const string LogFileSuffix = ".log";

    internal static string LogFileName => $"{LogFilePrefix}{Dependencies.Role}{LogFileSuffix}";

    static bool IsValidRole(string? role) =>
        !string.IsNullOrEmpty(role) && role.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static bool IsValidLogFileName(string fileName) =>
        fileName.Length > LogFilePrefix.Length + LogFileSuffix.Length &&
        fileName.StartsWith(LogFilePrefix, StringComparison.Ordinal) &&
        fileName.EndsWith(LogFileSuffix, StringComparison.Ordinal) &&
        IsValidRole(fileName[LogFilePrefix.Length..^LogFileSuffix.Length]);

    /// <summary>
    ///     Full path of this process's diagnostics log file. The client forwards it to the
    ///     elevated child as <c>--diag-log</c> so the broker uses its parent directory and
    ///     filters the log file's own journal entries out of the watch stream.
    /// </summary>
    internal static string LogPath =>
        ResolveLogPath(Path.Combine(Dependencies.LogDirectory, LogFileName), OperatingSystem.IsWindows());

    internal static string ResolveLogPath(string combined, bool isWindows)
    {
        if (isWindows)
        {
            return Path.GetFullPath(combined);
        }

        // On non-Windows platforms (e.g. Linux unit tests / probes), preserve Windows
        // volume or extended roots (e.g. "C:\..." or "\\?\C:\...") without prepending
        // the Unix working directory. If relative, resolve against the current directory.
        if (BrokerDiagnosticsLogFilter.TryGetDrive(combined, isWindows, out _))
        {
            return combined;
        }

        return Path.GetFullPath(combined);
    }

    /// <summary>
    ///     The client process's log path, forwarded with <c>--diag-log</c> so the broker can
    ///     drop the client log's journal entries too. Null when the client sent no path.
    /// </summary>
    internal static string? ClientLogPath
    {
        get => Dependencies.ForwardedClientLogPath;
        set => Dependencies.ForwardedClientLogPath = value;
    }

    /// <summary>
    ///     Set by <c>--diag-include-self</c> (or <c>MFTLIB_BROKER_DIAG_INCLUDE_SELF=1</c>): ship
    ///     the diagnostics logs' own journal entries instead of dropping them. For debugging
    ///     the diagnostics themselves.
    /// </summary>
    internal static bool IncludeSelfEntries
    {
        get => Dependencies.IncludeSelfRequested ||
               Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF") == "1";
        set => Dependencies.IncludeSelfRequested = value;
    }

    internal static bool Enabled => Dependencies.Forced ||
        (Dependencies.AcceptEnvironmentOptIn &&
         Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG") == "1");

    /// <summary>
    ///     Force diagnostics on for this process and tag its log lines with
    ///     <paramref name="role" />. Each caller process that wants its own log calls it with its own role:
    ///     the elevated broker child uses <see cref="EnableBroker" /> via child dispatch so its sink
    ///     exclusively creates <c>broker-diagnostics.broker.log</c>, while a consumer's client or
    ///     user-interface process calls <see cref="Enable" /> under its role and appends to
    ///     <c>broker-diagnostics.&lt;role&gt;.log</c> in the supplied directory.
    /// </summary>
    /// <param name="role">The nonempty role tag and file-name segment: ASCII letters, digits, dash and underscore only.</param>
    /// <param name="logDirectory">The caller-owned log directory. The caller creates it before logging.</param>
    /// <exception cref="ArgumentException">The role is not a valid file-name segment.</exception>
    public static void Enable(string role, string logDirectory)
    {
        if (!IsValidRole(role))
        {
            throw new ArgumentException("The diagnostics role must contain only ASCII letters, digits, dash and underscore.", nameof(role));
        }

        Dependencies.LogDirectory = logDirectory;
        Dependencies.Role = role;
        Dependencies.Forced = true;
    }

    internal static void EnableBroker(string logDirectory)
    {
        Enable("broker", logDirectory);
        Dependencies.InstallWriter(new BrokerDiagnosticsWriter(new BrokerDiagnosticsFileSink(LogPath), () => "broker"));
    }

    internal static Task CompleteAsync()
    {
        var writer = Dependencies.Writer;
        if (writer is null)
        {
            return Task.CompletedTask;
        }

        writer.Complete();
        return writer.Completion;
    }

    /// <summary>
    ///     Build the filter that drops both diagnostics log files' journal entries from
    ///     JournalBatch frames, or null when diagnostics are off (nothing is logged, so
    ///     nothing needs filtering) or <see cref="IncludeSelfEntries" /> opted the entries
    ///     back in.
    /// </summary>
    internal static BrokerDiagnosticsLogFilter? CreateLogFilter(
        Func<string, ulong?>? resolveFileReference = null)
    {
        return Enabled && !IncludeSelfEntries
            ? new BrokerDiagnosticsLogFilter(LogPath, ClientLogPath, resolveFileReference)
            : null;
    }

    /// <summary>
    ///     Process-global diagnostics state and substitutions: the opt-in, role, directories and
    ///     the writer. Reset after producers and drains stop.
    /// </summary>
    internal static readonly DiagnosticsDependencies Dependencies = new();

    internal sealed class DiagnosticsDependencies
    {
        // Held only for writer handoff and queue admission, never for sink I/O.
        readonly Lock _writerLock = new();

        BrokerDiagnosticsWriter? _writer;

        internal bool Forced { get; set; }

        internal bool AcceptEnvironmentOptIn { get; set; } = true;

        internal string Role { get; set; } = DefaultRole;

        internal string LogDirectory { get; set; } = Path.GetTempPath();

        internal string? ForwardedClientLogPath { get; set; }

        internal bool IncludeSelfRequested { get; set; }

        internal BrokerDiagnosticsWriter? Writer => Volatile.Read(ref _writer);

        internal void InstallWriter(BrokerDiagnosticsWriter writer)
        {
            lock (_writerLock)
            {
                Interlocked.Exchange(ref _writer, writer)?.Complete();
            }
        }

        internal void Enqueue(string line)
        {
            lock (_writerLock)
            {
                var writer = _writer ??= new BrokerDiagnosticsWriter(
                    queuedLine => File.AppendAllText(LogPath, queuedLine + Environment.NewLine),
                    () => Role);
                writer.TryEnqueue(line);
            }
        }

        internal void ResetToDefaults()
        {
            lock (_writerLock)
            {
                Interlocked.Exchange(ref _writer, null)?.Complete();
                Forced = false;
                AcceptEnvironmentOptIn = true;
                Role = DefaultRole;
                LogDirectory = Path.GetTempPath();
                ForwardedClientLogPath = null;
                IncludeSelfRequested = false;
            }
        }
    }

    /// <summary>The channel tag of the control pipe's lines.</summary>
    internal const string ControlChannel = "control";

    /// <summary>Completes once every line logged before this call has been appended or dropped.</summary>
    internal static Task FlushAsync(CancellationToken cancellationToken)
    {
        return Dependencies.Writer?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    ///     Queue one line for the log. The caller never waits for the file: the line is
    ///     formatted here (so its timestamp is the call's) and a background task appends it.
    /// </summary>
    internal static void Log(string channel, string message)
    {
        if (!Enabled)
        {
            return;
        }

        Dependencies.Enqueue($"{DateTime.UtcNow:O}  [{Dependencies.Role}:{Environment.ProcessId}:{channel}]  {message}");
    }

    /// <summary>
    ///     Trace one frame on the wire: its kind byte and total length (kind byte + payload).
    ///     A read that records an unexpected kind, preceded by a frame whose length does not
    ///     match its real content, pinpoints which writer desynced the stream.
    /// </summary>
    internal static void LogFrame(string channel, string direction, byte kind, int length)
    {
        Log(channel, $"frame {direction} kind={kind} len={length} t={Environment.CurrentManagedThreadId}");
    }
}
