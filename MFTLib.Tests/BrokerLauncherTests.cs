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
        BrokerLauncher.ResetToDefaults();
        _process?.Dispose();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_NullProcessPath_Throws()
    {
        BrokerLauncher._getProcessPathFunc = () => null;
        Assert.ThrowsException<InvalidOperationException>(() => BrokerLauncher.Launch("--broker"));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_ProcessStarts_ReturnsTrueAndDisposesProcess()
    {
        var process = new Process();
        _process = process;
        BrokerLauncher._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        BrokerLauncher._startProcess = _ => process;

        Assert.IsTrue(BrokerLauncher.Launch("--broker --pipe p"));
        Assert.ThrowsException<ObjectDisposedException>(() => _ = process.StandardInput, "The process wrapper must be disposed before Launch returns.");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_ProcessReturnsNull_ReturnsFalse()
    {
        BrokerLauncher._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        BrokerLauncher._startProcess = _ => null;

        Assert.IsFalse(BrokerLauncher.Launch("--broker --pipe p"));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Launch_Win32Exception1223_ReturnsFalse()
    {
        BrokerLauncher._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        BrokerLauncher._startProcess = _ => throw new Win32Exception(1223);

        Assert.IsFalse(BrokerLauncher.Launch("--broker --pipe p"));
    }
}
