using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class ElevationUtilitiesTests
{
    [TestCleanup]
    public void Cleanup()
    {
        ElevationUtilities.ResetToDefaults();
    }

    [TestMethod]
    public void IsElevated_ReturnsBool()
    {
        var result = ElevationUtilities.IsElevated();
        Assert.IsInstanceOfType<bool>(result);
    }

    [TestMethod]
    public void IsElevated_NonWindows_ReturnsFalse()
    {
        ElevationUtilities._isWindows = () => false;
        Assert.IsFalse(ElevationUtilities.IsElevated());
    }

    [TestMethod]
    public void GetProcessPath_ReturnsNonNull()
    {
        var path = ElevationUtilities.GetProcessPath();
        Assert.IsNotNull(path);
        Assert.IsTrue(path.Length > 0);
    }

    // --- CanSelfElevate ---

    [TestMethod]
    public void CanSelfElevate_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_EmptyProcessPath_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => "";
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_DotnetExe_ReturnsFalse()
    {
        // Use forward slashes so Path.GetFileNameWithoutExtension works on both Windows and Linux
        ElevationUtilities._getProcessPathFunc = () => "C:/dotnet/dotnet.exe";
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_NormalExe_ReturnsTrue()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        Assert.IsTrue(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_NotUserInteractive_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => false;
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    // --- TryRunElevated ---

    [TestMethod]
    public void TryRunElevated_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_DotnetExe_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\dotnet\dotnet.exe";
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_ProcessReturnsNull_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => null;
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_ProcessExitsZero_ReturnsTrue()
    {
        ElevationUtilities._getProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        // Use cross-platform command: 'true' on POSIX, 'cmd /c exit 0' on Windows
        ElevationUtilities._startProcess = _ => Process.Start(new ProcessStartInfo(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "true"
                    : "cmd.exe",
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? string.Empty
                    : "/c exit 0"
            )
        { CreateNoWindow = true });
        Assert.IsTrue(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_ProcessExitsNonZero_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 1")
        { CreateNoWindow = true });
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_Timeout_KillsProcessAndReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => Process.Start(LongRunningProcessStartInfo());
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test", 100));
    }

    [TestMethod]
    public void TryRunElevated_Win32Exception1223_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => throw new Win32Exception(1223);
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_GenericException_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => throw new InvalidOperationException("test");
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_NotUserInteractive_ReturnsFalseWithoutStartingProcess()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => false;
        ElevationUtilities._startProcess = _ => throw new InvalidOperationException("should not be called");
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    /// <summary>
    ///     A process that outlives a short timeout and that <see cref="Process.Kill()" /> fully
    ///     terminates. Deliberately not wrapped in <c>cmd.exe /c</c>: Kill() ends only the process
    ///     it is handed, so a wrapper leaves the real sleeper orphaned holding the inherited stdio
    ///     handles, which fails the CI step with "WaitDelay expired before I/O complete". Output is
    ///     redirected for the same reason.
    /// </summary>
    internal static ProcessStartInfo LongRunningProcessStartInfo()
    {
        var isPosix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                      RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        return new ProcessStartInfo(isPosix ? "sleep" : "ping.exe", isPosix ? "60" : "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }
}
