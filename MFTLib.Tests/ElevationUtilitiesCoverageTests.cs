using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
/// Additional ElevationUtilities tests targeting uncovered code paths on Linux.
/// These exercises TryRunElevated branches that may not be hit by the
/// cross-platform tests in ElevationUtilitiesTests.
/// </summary>
[TestClass]
public class ElevationUtilitiesCoverageTests
{
    [TestCleanup]
    public void Cleanup() => ElevationUtilities.ResetToDefaults();

    // --- TryRunElevated: dotnet check path (line 64) ---

    [TestMethod]
    public void TryRunElevated_DotnetExe_ReturnsFalse()
    {
        // Exercises the dotnet.exe check in TryRunElevated.
        // The process path is "dotnet" so the method returns false before entering the try block.
        ElevationUtilities.GetProcessPathFunc = () => "C:/dotnet/dotnet.exe";
        var result = ElevationUtilities.TryRunElevated("--test");
        Assert.IsFalse(result);
    }

    // --- TryRunElevated: exit code 0 path (line 87) ---

    [TestMethod]
    public void TryRunElevated_ExitCodeZero_ReturnsTrue()
    {
        // Exercises the exit code check at line 87 of TryRunElevated.
        // Uses a non-dotnet exe path so the dotnet check passes,
        // then mocks StartProcess to return a process that exits with code 0.
        ElevationUtilities.GetProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities.IsUserInteractive = () => true;
        ElevationUtilities.StartProcess = _ =>
        {
            var psi = new ProcessStartInfo(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "true" : "cmd.exe",
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? string.Empty : "/c exit 0"
            )
            { CreateNoWindow = true, UseShellExecute = false };
            return Process.Start(psi);
        };
        Assert.IsTrue(ElevationUtilities.TryRunElevated("--test"));
    }

    // --- TryRunElevated: exit code non-zero path (line 87) ---

    [TestMethod]
    public void TryRunElevated_ExitCodeNonZero_ReturnsFalse()
    {
        // Exercises the exit code check at line 87 of TryRunElevated for non-zero exit codes.
        ElevationUtilities.GetProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities.IsUserInteractive = () => true;
        ElevationUtilities.StartProcess = _ =>
        {
            var psi = new ProcessStartInfo(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "false" : "cmd.exe",
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? string.Empty : "/c exit 1"
            )
            { CreateNoWindow = true, UseShellExecute = false };
            return Process.Start(psi);
        };
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    // --- TryRunElevated: timeout path (lines 81, 83, 84) ---

    [TestMethod]
    public void TryRunElevated_Timeout_KillsProcessAndReturnsFalse()
    {
        // Exercises the timeout path at lines 81, 83, 84 of TryRunElevated.
        // Starts a process that takes longer than the timeout, so WaitForExit returns false,
        // then Kill() is called and the method returns false.
        ElevationUtilities.GetProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities.IsUserInteractive = () => true;
        ElevationUtilities.StartProcess = _ =>
        {
            // On Linux, sleep 60 should exceed the 100ms timeout
            var psi = new ProcessStartInfo(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "sleep" : "cmd.exe",
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "60" : "/c ping -n 30 127.0.0.1 >nul"
            )
            { CreateNoWindow = true, UseShellExecute = false };
            return Process.Start(psi);
        };
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test", timeoutMs: 100));
    }
}
