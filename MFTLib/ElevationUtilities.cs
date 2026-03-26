using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MFTLib;

public static class ElevationUtilities
{
    public static bool IsElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? GetProcessPath() => Environment.ProcessPath;

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
            // When running via 'dotnet <dll>', the first argument is the DLL path
            var allArgs = Environment.GetCommandLineArgs();
            startInfo.FileName = processPath;
            // Join all args starting from the second one (which is the DLL or the first app arg)
            startInfo.Arguments = string.Join(" ", allArgs.Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }
        else
        {
            var argsList = arguments ?? Environment.GetCommandLineArgs().Skip(1).ToArray();
            startInfo.FileName = processPath;
            startInfo.Arguments = string.Join(" ", argsList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }

        try
        {
            var process = Process.Start(startInfo);
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
