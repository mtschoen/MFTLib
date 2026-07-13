using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MFTLib;

public static class ElevationUtilities
{
    /// <summary>
    /// Default <see cref="IElevationProvider"/> backed by the static methods below.
    /// Consumers default to this and inject a fake in tests.
    /// </summary>
    public static IElevationProvider DefaultProvider { get; } = new DefaultElevationProvider();

    // Swappable dependencies for testability — tests replace these to exercise
    // defensive branches (non-Windows, null process path, process start failures)
    // that cannot be triggered in a normal Windows test environment.
    internal static Func<bool> IsWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    internal static Func<string?> GetProcessPathFunc = () => Environment.ProcessPath;
    internal static Func<ProcessStartInfo, Process?> StartProcess = Process.Start;
    internal static Func<bool> IsUserInteractive = () => Environment.UserInteractive;
    internal static void ResetToDefaults()
    {
        IsWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        GetProcessPathFunc = () => Environment.ProcessPath;
        StartProcess = Process.Start;
        IsUserInteractive = () => Environment.UserInteractive;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416", Justification = "Guarded by IsWindows() runtime check")]
    public static bool IsElevated()
    {
        if (!IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? GetProcessPath() => GetProcessPathFunc();

    /// <summary>
    /// Returns true if the current process can self-elevate via UAC — i.e., there is a
    /// resolvable executable path and the host is not dotnet.exe.
    /// </summary>
    public static bool CanSelfElevate()
    {
        var processPath = GetProcessPath();
        if (string.IsNullOrEmpty(processPath))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(processPath).ToLowerInvariant();
        return fileName != "dotnet";
    }

    /// <summary>
    /// Launch an elevated copy of this executable with the given arguments and wait
    /// for it to exit. Returns false if the process path is unavailable, the user
    /// declines UAC, the child process returns a non-zero exit code, the timeout
    /// elapses (in which case the child is killed), or there is no interactive
    /// session to host a UAC consent prompt (e.g. a Session 0 service host).
    /// </summary>
    public static bool TryRunElevated(string arguments, int timeoutMs = 60000)
    {
        var exePath = GetProcessPath();
        if (string.IsNullOrEmpty(exePath))
            return false;

        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return false;

        // ShellExecuteEx with the "runas" verb needs an interactive desktop to show
        // the UAC consent dialog. Without one (Session 0 services, CI runners) it
        // fails unpredictably instead of cleanly declining, so bail out up front.
        if (!IsUserInteractive())
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var process = StartProcess(startInfo);
            if (process == null)
                return false;

            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
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

internal sealed class DefaultElevationProvider : IElevationProvider
{
    public bool IsElevated() => ElevationUtilities.IsElevated();
    public bool CanSelfElevate() => ElevationUtilities.CanSelfElevate();
    public bool TryRunElevated(string arguments, int timeoutMs = 60000)
        => ElevationUtilities.TryRunElevated(arguments, timeoutMs);
}
