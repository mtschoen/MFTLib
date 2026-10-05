using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MFTLib;

/// <summary>
///     Elevation checks and the UAC self-relaunch helper that raw volume access needs.
/// </summary>
public static class ElevationUtilities
{
    // Swappable dependencies for testability - tests replace these to exercise
    // defensive branches (non-Windows, null process path, process start failures)
    // that cannot be triggered in a normal Windows test environment.
    internal static Func<bool> _isWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    internal static Func<string?> _getProcessPathFunc = () => Environment.ProcessPath;
    internal static Func<ProcessStartInfo, Process?> _startProcess = ElevationGuard.Start;
    internal static Func<bool> _isUserInteractive = () => Environment.UserInteractive;
    internal static Func<Process, TimeSpan, bool> _waitForExit = (process, timeout) => process.WaitForExit(timeout);
    internal static Action<Process> _killProcess = process => process.Kill();
    internal static Func<Process, int> _getExitCode = process => process.ExitCode;

    /// <summary>The wait <see cref="TryRunElevated" /> callers use when they have no reason to choose another: 60 seconds.</summary>
    public static readonly TimeSpan DefaultElevatedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Default <see cref="IElevationProvider" /> backed by the static methods below.
    ///     Consumers default to this and inject a fake in tests.
    /// </summary>
    public static IElevationProvider DefaultProvider { get; } = new DefaultElevationProvider();

    internal static void ResetToDefaults()
    {
        _isWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _getProcessPathFunc = () => Environment.ProcessPath;
        _startProcess = ElevationGuard.Start;
        _isUserInteractive = () => Environment.UserInteractive;
        _waitForExit = (process, timeout) => process.WaitForExit(timeout);
        _killProcess = process => process.Kill();
        _getExitCode = process => process.ExitCode;
    }

    /// <summary>Reports whether the current process holds the Administrator role.</summary>
    /// <returns>True when elevated; always false when the host is not Windows.</returns>
    [SuppressMessage("Interoperability", "CA1416", Justification = "Guarded by IsWindows() runtime check")]
    public static bool IsElevated()
    {
        if (!_isWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static string? GetProcessPath()
    {
        return _getProcessPathFunc();
    }

    /// <summary>
    ///     Returns true if the current process can self-elevate via UAC - i.e., there is a
    ///     resolvable executable path, the host is not dotnet.exe, and the session is user-interactive.
    /// </summary>
    public static bool CanSelfElevate()
    {
        var processPath = GetProcessPath();
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // ShellExecuteEx with the "runas" verb needs an interactive desktop to show
        // the UAC consent dialog. Without one (Session 0 services, CI runners) it
        // cannot self-elevate.
        if (!_isUserInteractive())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Launch an elevated copy of this executable with the given arguments and wait
    ///     for it to exit. Returns false if the process path is unavailable, the user
    ///     declines UAC, the child process returns a non-zero exit code, the timeout
    ///     elapses (in which case the child is killed), the timeout is unsupported by
    ///     <see cref="Process.WaitForExit(TimeSpan)" />, or there is no interactive
    ///     session to host a UAC consent prompt (e.g. a Session 0 service host).
    /// </summary>
    /// <param name="arguments">
    ///     Arguments for the elevated child, one element per argument. Each is quoted for the
    ///     child's command line, so an element may contain spaces or quotation marks.
    /// </param>
    /// <param name="timeout">
    ///     Maximum time to wait for the elevated child; <see cref="DefaultElevatedTimeout" /> is a sensible choice.
    ///     Must be between 0 and <see cref="int.MaxValue" /> milliseconds, or <see cref="Timeout.InfiniteTimeSpan" />.
    /// </param>
    public static bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var totalMilliseconds = (long)timeout.TotalMilliseconds;
        if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue || (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan))
        {
            return false;
        }

        var exePath = GetProcessPath();
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // ShellExecuteEx with the "runas" verb needs an interactive desktop to show
        // the UAC consent dialog. Without one (Session 0 services, CI runners) it
        // fails unpredictably instead of cleanly declining, so bail out up front.
        if (!_isUserInteractive())
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = _startProcess(startInfo);
            if (process == null)
            {
                return false;
            }

            if (!_waitForExit(process, timeout))
            {
                _killProcess(process);
                return false;
            }

            return _getExitCode(process) == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch (Exception exception) when (exception is not ElevationForbiddenException)
        {
            return false;
        }
    }
}

sealed class DefaultElevationProvider : IElevationProvider
{
    public bool IsElevated()
    {
        return ElevationUtilities.IsElevated();
    }

    public bool CanSelfElevate()
    {
        return ElevationUtilities.CanSelfElevate();
    }

    public bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        return ElevationUtilities.TryRunElevated(arguments, timeout);
    }
}
