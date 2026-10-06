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
    /// </summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns><c>true</c> when the process was relaunched as the broker and has served its session.</returns>
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
                    // --diag-log carries the client process's log path and
                    // --diag-include-self the opt-in to keep the logs' own journal
                    // entries, for the same reason. Without --diag they are meaningless:
                    // diagnostics are off, so nothing is filtered anyway.
                    if (HasFlag(arguments, "--diag"))
                    {
                        BrokerDiagnostics.Enable("broker");
                        BrokerDiagnostics.ClientLogPath = FindOption(arguments, "--diag-log");
                        BrokerDiagnostics.IncludeSelfEntries = HasFlag(arguments, "--diag-include-self");
                    }

                    runner.RunBroker(FindOption(arguments, "--pipe"));
                    return true;
            }
        }

        return false;
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
