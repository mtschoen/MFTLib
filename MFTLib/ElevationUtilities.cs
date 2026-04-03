using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MFTLib;

public static class ElevationUtilities
{
    // Swappable dependencies for testability — tests replace these to exercise
    // defensive branches (non-Windows, null process path, process start failures)
    // that cannot be triggered in a normal Windows test environment.
    internal static Func<bool> IsWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    internal static Func<string?> GetProcessPathFunc = () => Environment.ProcessPath;
    internal static Func<ProcessStartInfo, Process?> StartProcess = Process.Start;
    internal static void ResetToDefaults()
    {
        IsWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        GetProcessPathFunc = () => Environment.ProcessPath;
        StartProcess = Process.Start;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416",
        Justification = "Guarded by IsWindows() runtime check")]
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
    /// declines UAC, the child process returns a non-zero exit code, or the timeout
    /// elapses (in which case the child is killed).
    /// </summary>
    public static bool TryRunElevated(string arguments, int timeoutMs = 60000)
    {
        var exePath = GetProcessPath();
        if (string.IsNullOrEmpty(exePath))
            return false;

        // Cannot self-elevate when hosted by dotnet.exe
        if (!CanSelfElevate())
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
