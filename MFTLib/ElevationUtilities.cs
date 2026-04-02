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
    internal static Func<string[]> GetCommandLineArgs = Environment.GetCommandLineArgs;

    internal static void ResetToDefaults()
    {
        IsWindows = () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        GetProcessPathFunc = () => Environment.ProcessPath;
        StartProcess = Process.Start;
        GetCommandLineArgs = Environment.GetCommandLineArgs;
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

    public static bool EnsureElevated(string[]? arguments = null)
    {
        if (IsElevated())
            return true;

        var processPath = GetProcessPath();
        if (processPath == null)
            return false;

        var fileName = Path.GetFileNameWithoutExtension(processPath).ToLowerInvariant();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false
        };

        if (fileName == "dotnet")
        {
            var allArgs = GetCommandLineArgs();
            startInfo.FileName = processPath;
            startInfo.Arguments = string.Join(" ", allArgs.Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }
        else
        {
            var argsList = arguments ?? GetCommandLineArgs().Skip(1).ToArray();
            startInfo.FileName = processPath;
            startInfo.Arguments = string.Join(" ", argsList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }

        try
        {
            var process = StartProcess(startInfo);
            if (process == null)
                return false;

            process.WaitForExit();
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
