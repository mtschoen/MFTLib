using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // replaces the process-wide BrokerLauncher seams
public class BrokerLauncherTests
{
    Process? _process;

    [TestCleanup]
    public void Cleanup()
    {
        BrokerLauncher.Dependencies.ResetToDefaults();
        _process?.Dispose();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_NullProcessPath_Throws()
    {
        BrokerLauncher.Dependencies.GetProcessPath = () => null;
        BrokerLauncher.Dependencies.StartProcess = _ => throw new AssertFailedException("Missing path must fail before launch.");
        Assert.ThrowsException<InvalidOperationException>(() => BrokerLauncher.Launch("--broker"));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_ProcessStarts_ReturnsTrueAndDisposesProcess()
    {
        var process = new Process();
        _process = process;
        var executable = Path.Combine(Path.GetTempPath(), "broker-launch-" + Guid.NewGuid().ToString("N") + ".exe");
        ProcessStartInfo? launched = null;
        BrokerLauncher.Dependencies.GetProcessPath = () => executable;
        BrokerLauncher.Dependencies.StartProcess = startInfo =>
        {
            launched = startInfo;
            return process;
        };

        Assert.IsTrue(BrokerLauncher.Launch("--broker --pipe p"));
        Assert.IsNotNull(launched);
        Assert.AreEqual(executable, launched.FileName);
        Assert.AreEqual("--broker --pipe p", launched.Arguments);
        Assert.IsTrue(launched.UseShellExecute);
        Assert.AreEqual("runas", launched.Verb);
        Assert.AreEqual(ProcessWindowStyle.Hidden, launched.WindowStyle);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = process.StandardInput, "The process wrapper must be disposed before Launch returns.");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_ProcessReturnsNull_ReturnsFalse()
    {
        BrokerLauncher.Dependencies.GetProcessPath = () => @"C:\app\MyApp.exe";
        BrokerLauncher.Dependencies.StartProcess = _ => null;

        Assert.IsFalse(BrokerLauncher.Launch("--broker --pipe p"));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_Win32Exception1223_ReturnsFalse()
    {
        BrokerLauncher.Dependencies.GetProcessPath = () => @"C:\app\MyApp.exe";
        BrokerLauncher.Dependencies.StartProcess = _ => throw new Win32Exception(1223);

        Assert.IsFalse(BrokerLauncher.Launch("--broker --pipe p"));
    }
}
