using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
/// Launches the elevated journal broker: relaunches the current executable in
/// <c>--broker</c> mode under a UAC elevation prompt. The launch is fire-and-forget
/// (NOT waited on) - the broker is a long-lived process that the caller talks to over
/// a pipe, so waiting for it to exit (as <c>ElevationUtilities.TryRunElevated</c> does)
/// would be wrong here.
/// </summary>
public static class BrokerLauncher
{
    // Swappable dependencies for testability - a real UAC prompt and a real elevated
    // launch cannot be exercised from an in-process unit test, so tests substitute
    // these to reach the success, null-process, and declined-prompt branches.
    internal static Func<string?> GetProcessPathFunc = () => Environment.ProcessPath;
    internal static Func<ProcessStartInfo, Process?> StartProcess = Process.Start;

    internal static void ResetToDefaults()
    {
        GetProcessPathFunc = () => Environment.ProcessPath;
        StartProcess = Process.Start;
    }

    /// <summary>
    /// Start the broker with <paramref name="brokerArgs"/> (e.g. "--broker --pipe NAME").
    /// Returns true if the process started, false if the user declined the UAC prompt.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Launch(string brokerArgs)
    {
        var exePath = GetProcessPathFunc()
            ?? throw new InvalidOperationException("Cannot determine the current executable path to launch the broker");

        var startInfo = new ProcessStartInfo(exePath, brokerArgs)
        {
            // UseShellExecute is required to request the "runas" verb (UAC elevation);
            // the elevated child runs without a window of its own.
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            var process = StartProcess(startInfo);
            return process != null;
        }
        catch (Win32Exception)
        {
            // ERROR_CANCELLED (1223): the user dismissed the UAC prompt. The caller
            // surfaces this as "broker unavailable" rather than a crash.
            return false;
        }
    }
}
