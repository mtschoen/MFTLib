using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class ElevationUtilitiesTests
{
    [TestCleanup]
    public void Cleanup() => ElevationUtilities.ResetToDefaults();

    [TestMethod]
    public void IsElevated_ReturnsBool()
    {
        var result = ElevationUtilities.IsElevated();
        Assert.IsInstanceOfType(result, typeof(bool));
    }

    [TestMethod]
    public void IsElevated_NonWindows_ReturnsFalse()
    {
        ElevationUtilities.IsWindows = () => false;
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
        ElevationUtilities.GetProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_EmptyProcessPath_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => "";
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_DotnetExe_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\dotnet\dotnet.exe";
        Assert.IsFalse(ElevationUtilities.CanSelfElevate());
    }

    [TestMethod]
    public void CanSelfElevate_NormalExe_ReturnsTrue()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        Assert.IsTrue(ElevationUtilities.CanSelfElevate());
    }

    // --- TryRunElevated ---

    [TestMethod]
    public void TryRunElevated_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_DotnetExe_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\dotnet\dotnet.exe";
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_ProcessReturnsNull_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => null;
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_ProcessExitsZero_ReturnsTrue()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true });
        Assert.IsTrue(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_ProcessExitsNonZero_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 1") { CreateNoWindow = true });
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_Timeout_KillsProcessAndReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true });
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test", timeoutMs: 100));
    }

    [TestMethod]
    public void TryRunElevated_Win32Exception1223_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => throw new System.ComponentModel.Win32Exception(1223);
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

    [TestMethod]
    public void TryRunElevated_GenericException_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => throw new InvalidOperationException("test");
        Assert.IsFalse(ElevationUtilities.TryRunElevated("--test"));
    }

}
