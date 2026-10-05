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
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_DotnetExe_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => "C:/dotnet/dotnet.exe";
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_ProcessReturnsNull_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => null;
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [DataTestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    public void TryRunElevated_ProcessExit_ReturnsTrueOnlyForZero(int exitCode, bool expected)
    {
        ArrangeFakeProcess(waitResult: true, exitCode, out var killed);
        Assert.AreEqual(expected, ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
        Assert.AreEqual(0, killed.Count, "A process that exited is never killed.");
    }

    [TestMethod]
    public void TryRunElevated_PassesEachArgumentAsOneElementWithoutAPreJoinedCommandLine()
    {
        ProcessStartInfo? started = null;
        ArrangeFakeProcess(waitResult: true, exitCode: 0, out _);
        ElevationUtilities._startProcess = startInfo =>
        {
            started = startInfo;
            return new Process();
        };

        Assert.IsTrue(ElevationUtilities.TryRunElevated(
            ["Program Files", "say \"hi\"", "C:\\spaced dir\\"], ElevationUtilities.DefaultElevatedTimeout));

        Assert.IsNotNull(started);
        CollectionAssert.AreEqual(new[] { "Program Files", "say \"hi\"", "C:\\spaced dir\\" }, started.ArgumentList.ToArray());
        Assert.AreEqual(string.Empty, started.Arguments, "ArgumentList and Arguments are mutually exclusive.");
        Assert.AreEqual("runas", started.Verb);
        Assert.IsTrue(started.UseShellExecute);
    }

    [TestMethod]
    public void DefaultElevatedTimeout_IsSixtySeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(60), ElevationUtilities.DefaultElevatedTimeout);
    }

    [DataTestMethod]
    [DataRow(1230000L /* 123 ms */, true, 1, DisplayName = "supported-timeout-waits-and-kills")]
    [DataRow(-10000L /* Timeout.InfiniteTimeSpan.Ticks */, true, 1, DisplayName = "supported-infinite-timeout-waits-and-kills")]
    [DataRow(25920000000000L /* 30 days */, false, 0, DisplayName = "unsupported-thirty-days-never-starts")]
    [DataRow(3155378975999999999L /* TimeSpan.MaxValue.Ticks */, false, 0, DisplayName = "unsupported-maxvalue-never-starts")]
    [DataRow(-20000L /* -2 ms */, false, 0, DisplayName = "unsupported-negative-never-starts")]
    public void TryRunElevated_Timeout_KillsProcessAndReturnsFalse(long ticks, bool expectStarted, int expectedKills)
    {
        var timeout = new TimeSpan(ticks);
        var startedCount = 0;
        var waited = new List<TimeSpan>();
        ArrangeFakeProcess(waitResult: false, exitCode: 0, out var killed);
        var start = ElevationUtilities._startProcess;
        ElevationUtilities._startProcess = startInfo =>
        {
            startedCount++;
            return start(startInfo);
        };
        var wait = ElevationUtilities._waitForExit;
        ElevationUtilities._waitForExit = (process, timeoutPassed) =>
        {
            waited.Add(timeoutPassed);
            return wait(process, timeoutPassed);
        };

        var result = ElevationUtilities.TryRunElevated(["--test"], timeout);

        Assert.IsFalse(result);
        if (expectStarted)
        {
            Assert.AreEqual(1, startedCount, "Supported timeout starts the child process.");
            CollectionAssert.AreEqual(new[] { timeout }, waited);
            Assert.AreEqual(expectedKills, killed.Count, "The process that outlived its timeout is killed once.");
        }
        else
        {
            Assert.AreEqual(0, startedCount, "Unsupported timeout must never launch a child.");
            Assert.AreEqual(0, waited.Count, "Unsupported timeout must never wait.");
            Assert.AreEqual(0, killed.Count, "Never-started process is never killed.");
        }
    }

    [TestMethod]
    public void TryRunElevated_TimeoutAndKillFails_ReturnsFalse()
    {
        ArrangeFakeProcess(waitResult: false, exitCode: 0, out _);
        ElevationUtilities._killProcess = _ => throw new InvalidOperationException("already exited");
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_Win32Exception1223_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => throw new Win32Exception(1223);
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_GenericException_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => throw new InvalidOperationException("test");
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_NotUserInteractive_ReturnsFalseWithoutStartingProcess()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => false;
        ElevationUtilities._startProcess = _ => throw new InvalidOperationException("should not be called");
        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
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
