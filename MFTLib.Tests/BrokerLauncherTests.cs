using System.ComponentModel;
using System.Diagnostics;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // replaces the process-wide BrokerLauncher seams
public class BrokerLauncherTests
{
    Process? _process;

    [TestInitialize]
    public void Initialize()
    {
        BrokerLauncher.Dependencies._elevationProvider = new DefaultElevationProvider(new ElevationSeams
        {
            IsWindows = () => true,
            GetProcessPath = () => "C:/app/MyApp.exe",
            IsUserInteractive = () => true
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        BrokerLauncher.Dependencies.ResetToDefaults();
        _process?.Dispose();
    }

    [TestMethod]
    [DataRow("dotnet.exe", true, true, DisplayName = "issue-760-dotnet-host")]
    [DataRow("MyApp.exe", false, true, DisplayName = "issue-760-non-windows")]
    [DataRow("MyApp.exe", true, false, DisplayName = "issue-760-non-interactive")]
    public void Launch_CannotSelfElevate_ReturnsFalseWithoutStartingProcess(string executable, bool isWindows, bool isInteractive)
    {
        var started = false;
        BrokerLauncher.Dependencies._getProcessPath = () => "C:/app/" + executable;
        BrokerLauncher.Dependencies._elevationProvider = new DefaultElevationProvider(new ElevationSeams
        {
            IsWindows = () => isWindows,
            GetProcessPath = BrokerLauncher.Dependencies._getProcessPath,
            IsUserInteractive = () => isInteractive
        });
        BrokerLauncher.Dependencies._startProcess = _ =>
        {
            started = true;
            return null;
        };

        Assert.IsFalse(BrokerLauncher.StartElevated("--broker --pipe p"));
        Assert.IsFalse(started, "An unavailable elevation route must return before any process starts.");
    }

    [TestMethod]
    public void Launch_NullProcessPath_Throws()
    {
        BrokerLauncher.Dependencies._getProcessPath = () => null;
        BrokerLauncher.Dependencies._startProcess = _ => throw new AssertFailedException("Missing path must fail before launch.");
        Assert.ThrowsExactly<InvalidOperationException>(() => BrokerLauncher.StartElevated("--broker"));
    }

    [TestMethod]
    public void Launch_ProcessStarts_ReturnsTrueAndDisposesProcess()
    {
        var process = new Process();
        _process = process;
        using var directory = new TempDirectory("broker-launch", create: false);
        var executable = Path.Combine(directory.Path, "broker-launch.exe");
        ProcessStartInfo? launched = null;
        BrokerLauncher.Dependencies._getProcessPath = () => executable;
        BrokerLauncher.Dependencies._startProcess = startInfo =>
        {
            launched = startInfo;
            return process;
        };

        Assert.IsTrue(BrokerLauncher.StartElevated("--broker --pipe p"));
        Assert.IsNotNull(launched);
        Assert.AreEqual(executable, launched.FileName);
        Assert.AreEqual("--broker --pipe p", launched.Arguments);
        Assert.IsTrue(launched.UseShellExecute);
        Assert.AreEqual("runas", launched.Verb);
        Assert.AreEqual(ProcessWindowStyle.Hidden, launched.WindowStyle);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = process.StandardInput, "The process wrapper must be disposed before Launch returns.");
    }

    [TestMethod]
    public void Launch_ProcessReturnsNull_ReturnsFalse()
    {
        BrokerLauncher.Dependencies._getProcessPath = () => @"C:\app\MyApp.exe";
        BrokerLauncher.Dependencies._startProcess = _ => null;

        Assert.IsFalse(BrokerLauncher.StartElevated("--broker --pipe p"));
    }

    [TestMethod]
    public void Launch_Win32Exception1223_ReturnsFalse()
    {
        BrokerLauncher.Dependencies._getProcessPath = () => @"C:\app\MyApp.exe";
        BrokerLauncher.Dependencies._startProcess = _ => throw new Win32Exception(1223);

        Assert.IsFalse(BrokerLauncher.StartElevated("--broker --pipe p"));
    }
}
