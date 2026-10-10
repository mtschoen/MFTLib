namespace MFTLib;

/// <summary>
///     Shared dispatch for the elevated broker child-process mode. When the process was
///     relaunched with <c>--broker</c>, <see cref="TryHandle(string[])" /> parses the arguments,
///     runs the broker, and returns <c>true</c> so the caller short-circuits its normal
///     startup. A normal launch matches no mode flag and returns <c>false</c>.
/// </summary>
public static class ElevatedEntryPoint
{
    /// <summary>
    ///     Dispatch the <c>--broker</c> flag in <paramref name="arguments" />, if present, by running the
    ///     elevated broker. Returns <c>true</c> if the broker was handled, <c>false</c> for a normal
    ///     launch. The caller passes the full process arguments; a leading executable path (as in
    ///     <see cref="System.Environment.GetCommandLineArgs" />) is simply skipped because it matches
    ///     no flag.
    ///     With <c>--diag</c>, <c>--diag-log</c> must be a normalized, fully qualified path
    ///     named <c>broker-diagnostics.log</c> that does not name an existing directory, without
    ///     invalid characters or reserved directory names in the ordinary Windows namespace.
    ///     Extended-length Windows paths allow literal reserved names. Rejected arguments exit
    ///     with code 1 without running the broker.
    /// </summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns><c>true</c> when broker mode was handled, including rejected diagnostics arguments.</returns>
    public static bool TryHandle(string[] arguments)
    {
        return TryHandle(arguments, new DefaultElevatedEntryRunner());
    }

    // The same dispatch with the runner chosen by the caller, so tests substitute it.
    internal static bool TryHandle(string[] arguments, IElevatedEntryRunner runner)
    {
        foreach (var arg in arguments)
        {
            switch (arg)
            {
                case "--broker":
                    // The broker is reactive: drives, pipe names, cursors and section names
                    // arrive over the control pipe, so it needs only that pipe's name.
                    // --diag turns on frame tracing in the elevated child too (a runas
                    // launch does not reliably inherit the MFTLIB_BROKER_DIAG env var).
                    // --diag-log carries the client process's log path and its directory,
                    // --diag-include-self the opt-in to keep the logs' own journal
                    // entries, for the same reason. Without --diag they are meaningless:
                    // diagnostics are off, so nothing is filtered anyway.
                    if (HasFlag(arguments, "--diag"))
                    {
                        var clientLogPath = FindOption(arguments, "--diag-log");
                        var logDirectory = DiagnosticsDirectory(clientLogPath);
                        if (logDirectory == null)
                        {
                            DefaultElevatedEntryRunner._exitProcess(1);
                            return true;
                        }

                        BrokerDiagnostics.Enable("broker", logDirectory);
                        BrokerDiagnostics.ClientLogPath = clientLogPath;
                        BrokerDiagnostics.IncludeSelfEntries = HasFlag(arguments, "--diag-include-self");
                    }

                    runner.RunBroker(FindOption(arguments, "--pipe"));
                    return true;
            }
        }

        return false;
    }

    // The existing absolute log path transports the directory across runas as well as
    // identifying the client's file for journal self-filtering. Never resolve a relative path here.
    static string? DiagnosticsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            Path.GetFileName(path) != BrokerDiagnostics.LogFileName || Directory.Exists(path))
        {
            return null;
        }

        try
        {
            if (!string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal))
            {
                return null;
            }

            // Extended-length Windows paths skip normalization, so validate their components too.
            var restrictReservedNames = OperatingSystem.IsWindows() &&
                                        !path.StartsWith(@"\\?\", StringComparison.Ordinal);
            var components = path[Path.GetPathRoot(path.AsSpan()).Length..]
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return components.Any(component => component is "." or ".." ||
                       component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                       restrictReservedNames && IsReservedDirectoryName(component))
                ? null
                : Path.GetDirectoryName(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                         IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    static bool IsReservedDirectoryName(string component)
    {
        var name = component.Split('.')[0].TrimEnd(' ');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
               name.Length == 4 &&
               (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (name[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    // Return the value following the first occurrence of name, or null if absent / last.
    static string? FindOption(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index < arguments.Length - 1 ? arguments[index + 1] : null;
    }

    static bool HasFlag(string[] arguments, string name)
    {
        return Array.IndexOf(arguments, name) >= 0;
    }
}
