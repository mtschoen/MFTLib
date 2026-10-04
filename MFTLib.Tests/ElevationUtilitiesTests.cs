using System.ComponentModel;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
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
        ElevationUtilities._getProcessPathFunc = () => "C:/dotnet/dotnet.exe";
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

    [DataTestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    public void TryRunElevated_ProcessExit_ReturnsTrueOnlyForZero(int exitCode, bool expected)
    {
        ArrangeFakeProcess(waitResult: true, exitCode, out var killed);
        Assert.AreEqual(expected, ElevationUtilities.TryRunElevated("--test"));
        Assert.AreEqual(0, killed.Count, "A process that exited is never killed.");
    }

    [TestMethod]
    public void TryRunElevated_Timeout_KillsProcessAndReturnsFalse()
    {
        var waitedMilliseconds = new List<int>();
        ArrangeFakeProcess(waitResult: false, exitCode: 0, out var killed);
        var wait = ElevationUtilities._waitForExit;
        ElevationUtilities._waitForExit = (process, timeoutMs) =>
        {
            waitedMilliseconds.Add(timeoutMs);
            return wait(process, timeoutMs);
        };

        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test", 123));

        CollectionAssert.AreEqual(new[] { 123 }, waitedMilliseconds);
        Assert.AreEqual(1, killed.Count, "The process that outlived its timeout is killed once.");
    }

    [TestMethod]
    public void TryRunElevated_TimeoutAndKillFails_ReturnsFalse()
    {
        ArrangeFakeProcess(waitResult: false, exitCode: 0, out _);
        ElevationUtilities._killProcess = _ => throw new InvalidOperationException("already exited");
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
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

    // A never-started Process is only a handle: the wait, kill and exit-code seams stand in for it.
    static void ArrangeFakeProcess(bool waitResult, int exitCode, out List<Process> killed)
    {
        var killedProcesses = new List<Process>();
        killed = killedProcesses;
        ElevationUtilities._getProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => new Process();
        ElevationUtilities._waitForExit = (_, _) => waitResult;
        ElevationUtilities._killProcess = killedProcesses.Add;
        ElevationUtilities._getExitCode = _ => exitCode;
    }
}
