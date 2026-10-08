using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MFTLib;

sealed class DefaultElevationProvider : IElevationProvider
{
    // Swappable dependencies for testability - tests build a provider with fakes to exercise
    // defensive branches (non-Windows, null process path, process start failures)
    // that cannot be triggered in a normal Windows test environment.
    readonly Func<bool> _isWindows;
    readonly Func<string?> _getProcessPath;
    readonly Func<ProcessStartInfo, Process?> _startProcess;
    readonly Func<bool> _isUserInteractive;
    readonly Func<Process, TimeSpan, bool> _waitForExit;
    readonly Action<Process> _killProcess;
    readonly Func<Process, int> _getExitCode;

    internal DefaultElevationProvider(ElevationSeams? seams = null)
    {
        seams ??= new ElevationSeams();
        _isWindows = seams.IsWindows ?? (() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        _getProcessPath = seams.GetProcessPath ?? (() => Environment.ProcessPath);
        _startProcess = seams.StartProcess ?? Process.Start;
        _isUserInteractive = seams.IsUserInteractive ?? (() => Environment.UserInteractive);
        _waitForExit = seams.WaitForExit ?? ((process, timeout) => process.WaitForExit(timeout));
        _killProcess = seams.KillProcess ?? (process => process.Kill());
        _getExitCode = seams.GetExitCode ?? (process => process.ExitCode);
    }

    [SuppressMessage("Interoperability", "CA1416", Justification = "Guarded by the _isWindows runtime check")]
    public bool IsElevated()
    {
        if (!_isWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool CanSelfElevate()
    {
        var processPath = _getProcessPath();
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

    public bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var totalMilliseconds = (long)timeout.TotalMilliseconds;
        if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue || (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan))
        {
            return false;
        }

        var exePath = _getProcessPath();
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
        catch
        {
            return false;
        }
    }
}

/// <summary>Replacements for the provider's platform and process dependencies; a null member keeps the production behavior.</summary>
sealed record ElevationSeams
{
    internal Func<bool>? IsWindows { get; init; }
    internal Func<string?>? GetProcessPath { get; init; }
    internal Func<ProcessStartInfo, Process?>? StartProcess { get; init; }
    internal Func<bool>? IsUserInteractive { get; init; }
    internal Func<Process, TimeSpan, bool>? WaitForExit { get; init; }
    internal Action<Process>? KillProcess { get; init; }
    internal Func<Process, int>? GetExitCode { get; init; }
}
