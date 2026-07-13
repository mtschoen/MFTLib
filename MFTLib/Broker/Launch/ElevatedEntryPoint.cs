namespace MFTLib;

/// <summary>
/// Shared dispatch for the elevated broker child-process mode. When the process was
/// relaunched with <c>--broker</c>, <see cref="TryHandle"/> parses the arguments,
/// runs the broker via the given <see cref="IElevatedEntryRunner"/>, and returns
/// <c>true</c> so the caller short-circuits its normal startup. A normal launch
/// matches no mode flag and returns <c>false</c>.
/// </summary>
public static class ElevatedEntryPoint
{
    /// <summary>
    /// Dispatch the <c>--broker</c> flag in <paramref name="args"/>, if present.
    /// Returns <c>true</c> if the broker was handled (the runner was invoked), <c>false</c>
    /// for a normal launch. The caller passes the full process arguments; a leading
    /// executable path (as in <see cref="System.Environment.GetCommandLineArgs"/>) is
    /// simply skipped because it matches no flag.
    /// </summary>
    public static bool TryHandle(string[] args, IElevatedEntryRunner runner)
    {
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--broker":
                    // The broker is reactive: drives, cursors, and map names arrive in
                    // ArmAndScan frames over the pipe, so it needs only the pipe name.
                    // --diag turns on frame tracing in the elevated child too (a runas
                    // launch does not reliably inherit the MFTLIB_BROKER_DIAG env var).
                    if (HasFlag(args, "--diag"))
                        BrokerDiagnostics.Enable("broker");
                    runner.RunBroker(FindOption(args, "--pipe"), HasFlag(args, "--once"));
                    return true;
            }
        }

        return false;
    }

    // Return the value following the first occurrence of name, or null if absent / last.
    static string? FindOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index < args.Length - 1 ? args[index + 1] : null;
    }

    static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
