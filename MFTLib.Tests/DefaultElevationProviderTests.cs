using System.ComponentModel;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Covers <see cref="ElevationUtilities.DefaultProvider" /> and the provider logic behind it,
///     driven through the internal constructor's fakes so no real UAC prompt or process is involved.
/// </summary>
[TestClass]
public class DefaultElevationProviderTests
{
    const string NormalExecutable = @"C:\app\MyApp.exe";

    [TestMethod]
    public void DefaultProvider_IsSingleton()
    {
        Assert.AreSame(ElevationUtilities.DefaultProvider, ElevationUtilities.DefaultProvider);
    }

    [TestMethod]
    public void DefaultProvider_IsProductionProvider()
    {
        Assert.IsInstanceOfType<DefaultElevationProvider>(ElevationUtilities.DefaultProvider);
    }

    [TestMethod]
    public void DefaultProvider_IsElevated_ReturnsBool()
    {
        var result = ElevationUtilities.DefaultProvider.IsElevated();
        Assert.IsInstanceOfType<bool>(result);
    }

    [TestMethod]
    public void DefaultElevatedTimeout_IsSixtySeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(60), ElevationUtilities.DefaultElevatedTimeout);
    }

    // --- IsElevated ---

    [TestMethod]
    public void IsElevated_NonWindows_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { IsWindows = () => false });
        Assert.IsFalse(provider.IsElevated());
    }

    // --- CanSelfElevate ---

    [TestMethod]
    public void CanSelfElevate_NullProcessPath_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => null });
        Assert.IsFalse(provider.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_EmptyProcessPath_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => "" });
        Assert.IsFalse(provider.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_DotnetExe_ReturnsFalse()
    {
        // Use forward slashes so Path.GetFileNameWithoutExtension works on both Windows and Linux
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => "C:/dotnet/dotnet.exe" });
        Assert.IsFalse(provider.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_NormalExe_ReturnsTrue()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => NormalExecutable, IsUserInteractive = () => true });
        Assert.IsTrue(provider.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_NotUserInteractive_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => NormalExecutable, IsUserInteractive = () => false });
        Assert.IsFalse(provider.CanSelfElevate());
    }

    // --- TryRunElevated ---

    [TestMethod]
    public void TryRunElevated_NullProcessPath_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => null });
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_DotnetExe_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => "C:/dotnet/dotnet.exe" });
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_ProcessReturnsNull_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams { GetProcessPath = () => NormalExecutable, IsUserInteractive = () => true, StartProcess = _ => null });
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [DataTestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    public void TryRunElevated_ProcessExit_ReturnsTrueOnlyForZero(int exitCode, bool expected)
    {
        var provider = CreateFakeProcessProvider(waitResult: true, exitCode, out var killed);
        Assert.AreEqual(expected, provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
        Assert.AreEqual(0, killed.Count, "A process that exited is never killed.");
    }

    [TestMethod]
    public void TryRunElevated_PassesEachArgumentAsOneElementWithoutAPreJoinedCommandLine()
    {
        ProcessStartInfo? started = null;
        var provider = CreateFakeProcessProvider(
            waitResult: true,
            exitCode: 0,
            out _,
            startProcess: startInfo =>
            {
                started = startInfo;
                return new Process();
            });

        Assert.IsTrue(provider.TryRunElevated(
            ["Program Files", "say \"hi\"", "C:\\spaced dir\\"], ElevationUtilities.DefaultElevatedTimeout));

        Assert.IsNotNull(started);
        CollectionAssert.AreEqual(new[] { "Program Files", "say \"hi\"", "C:\\spaced dir\\" }, started.ArgumentList.ToArray());
        Assert.AreEqual(string.Empty, started.Arguments, "ArgumentList and Arguments are mutually exclusive.");
        Assert.AreEqual("runas", started.Verb);
        Assert.IsTrue(started.UseShellExecute);
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
        var provider = CreateFakeProcessProvider(
            waitResult: false,
            exitCode: 0,
            out var killed,
            startProcess: _ =>
            {
                startedCount++;
                return new Process();
            },
            waitForExit: (_, timeoutPassed) =>
            {
                waited.Add(timeoutPassed);
                return false;
            });

        var result = provider.TryRunElevated(["--test"], timeout);

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
        var provider = CreateFakeProcessProvider(
            waitResult: false,
            exitCode: 0,
            out _,
            killProcess: _ => throw new InvalidOperationException("already exited"));
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_Win32Exception1223_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams
        {
            GetProcessPath = () => NormalExecutable,
            IsUserInteractive = () => true,
            StartProcess = _ => throw new Win32Exception(1223)
        });
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_GenericException_ReturnsFalse()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams
        {
            GetProcessPath = () => NormalExecutable,
            IsUserInteractive = () => true,
            StartProcess = _ => throw new InvalidOperationException("test")
        });
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_NotUserInteractive_ReturnsFalseWithoutStartingProcess()
    {
        var provider = new DefaultElevationProvider(new ElevationSeams
        {
            GetProcessPath = () => NormalExecutable,
            IsUserInteractive = () => false,
            StartProcess = _ => throw new InvalidOperationException("should not be called")
        });
        Assert.IsFalse(provider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    // A never-started Process is only a handle: the wait, kill and exit-code fakes stand in for it.
    static DefaultElevationProvider CreateFakeProcessProvider(
        bool waitResult,
        int exitCode,
        out List<Process> killed,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<Process, TimeSpan, bool>? waitForExit = null,
        Action<Process>? killProcess = null)
    {
        var killedProcesses = new List<Process>();
        killed = killedProcesses;
        return new DefaultElevationProvider(new ElevationSeams
        {
            GetProcessPath = () => "C:/app/MyApp.exe",
            IsUserInteractive = () => true,
            StartProcess = startProcess ?? (_ => new Process()),
            WaitForExit = waitForExit ?? ((_, _) => waitResult),
            KillProcess = killProcess ?? killedProcesses.Add,
            GetExitCode = _ => exitCode
        });
    }
}
