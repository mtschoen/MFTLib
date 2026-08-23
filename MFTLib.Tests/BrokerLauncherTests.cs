using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerLauncherTests
{
    [TestCleanup]
    public void Cleanup()
    {
        BrokerLauncher.ResetToDefaults();
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
    public void Launch_ProcessStarts_ReturnsTrue()
    {
        BrokerLauncher._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        // GetCurrentProcess() is a live Process handle obtained without spawning a
        // child - enough to exercise the "process != null" success path with no
        // real UAC prompt or elevated launch.
        BrokerLauncher._startProcess = _ => Process.GetCurrentProcess();

        Assert.IsTrue(BrokerLauncher.Launch("--broker --pipe p"));
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
