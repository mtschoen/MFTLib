using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
///     Launches the elevated journal broker: relaunches the current executable in
///     <c>--broker</c> mode under a UAC elevation prompt. The launch is fire-and-forget
///     (NOT waited on) - the broker is a long-lived process that the caller talks to over
///     a pipe, so waiting for it to exit (as <c>IElevationProvider.TryRunElevated</c> does)
///     would be wrong here.
/// </summary>
public static class BrokerLauncher
{
    /// <summary>Process-global launch substitutions; isolated tests reset after all launches drain.</summary>
    internal static readonly LaunchDependencies Dependencies = new();

    internal sealed class LaunchDependencies
    {
        internal Func<string?> _getProcessPath = () => Environment.ProcessPath;
        internal Func<ProcessStartInfo, Process?> _startProcess = Process.Start;

        internal void ResetToDefaults()
        {
            _getProcessPath = () => Environment.ProcessPath;
            _startProcess = Process.Start;
        }
    }

    /// <summary>
    ///     Start the broker with <paramref name="brokerArguments" /> (e.g. "--broker --pipe NAME").
    ///     Disposes the process wrapper without waiting for or terminating the broker.
    /// </summary>
    /// <param name="brokerArguments">The broker mode and pipe name arguments.</param>
    /// <returns>True if the process started; false if the user declined the UAC prompt or no process could be started.</returns>
    /// <exception cref="InvalidOperationException">The current executable path cannot be determined.</exception>
    [SupportedOSPlatform("windows")]
    public static bool Launch(string brokerArguments)
    {
        var exePath = Dependencies._getProcessPath()
                      ?? throw new InvalidOperationException(
                          "Cannot determine the current executable path to launch the broker");

        var startInfo = new ProcessStartInfo(exePath, brokerArguments)
        {
            // UseShellExecute is required to request the "runas" verb (UAC elevation);
            // the elevated child runs without a window of its own.
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Dependencies._startProcess(startInfo);
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
