using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
/// Covers the public <see cref="IElevationProvider"/> surface and
/// <see cref="ElevationUtilities.DefaultProvider"/>'s delegation to the statics,
/// driven through the existing internal Func seams.
/// </summary>
[TestClass]
public class ElevationProviderTests
{
    [TestCleanup]
    public void Cleanup() => ElevationUtilities.ResetToDefaults();

    [TestMethod]
    public void DefaultProvider_IsSingleton()
        => Assert.AreSame(ElevationUtilities.DefaultProvider, ElevationUtilities.DefaultProvider);

    [TestMethod]
    public void DefaultProvider_IsElevationProvider()
        => Assert.IsInstanceOfType(ElevationUtilities.DefaultProvider, typeof(IElevationProvider));

    [TestMethod]
    public void DefaultProvider_IsElevated_DelegatesToStatic()
    {
        ElevationUtilities.IsWindows = () => false;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.IsElevated());
    }

    [TestMethod]
    public void DefaultProvider_CanSelfElevate_NormalExe_ReturnsTrue()
    {
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        Assert.IsTrue(ElevationUtilities.DefaultProvider.CanSelfElevate());
    }

    [TestMethod]
    public void DefaultProvider_CanSelfElevate_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.CanSelfElevate());
    }

    [TestMethod]
    public void DefaultProvider_TryRunElevated_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities.GetProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.TryRunElevated("--test"));
    }

    [TestMethod]
    public void DefaultProvider_TryRunElevated_ProcessExitsZero_ReturnsTrue()
    {
        var isPosix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        ElevationUtilities.GetProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities.StartProcess = _ => Process.Start(new ProcessStartInfo(
            isPosix ? "true" : "cmd.exe",
            isPosix ? string.Empty : "/c exit 0"
        ) { CreateNoWindow = true, UseShellExecute = false });
        Assert.IsTrue(ElevationUtilities.DefaultProvider.TryRunElevated("--test"));
    }
}
