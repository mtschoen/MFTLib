using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Covers the public <see cref="IElevationProvider" /> surface and
///     <see cref="ElevationUtilities.DefaultProvider" />'s delegation to the statics,
///     driven through the existing internal Func seams.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ElevationProviderTests
{
    [TestCleanup]
    public void Cleanup()
    {
        ElevationUtilities.ResetToDefaults();
    }

    [TestMethod]
    public void DefaultProvider_IsSingleton()
    {
        Assert.AreSame(ElevationUtilities.DefaultProvider, ElevationUtilities.DefaultProvider);
    }

    [TestMethod]
    public void DefaultProvider_IsElevationProvider()
    {
        Assert.IsInstanceOfType<IElevationProvider>(ElevationUtilities.DefaultProvider);
    }

    [TestMethod]
    public void DefaultProvider_IsElevated_DelegatesToStatic()
    {
        ElevationUtilities._isWindows = () => false;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.IsElevated());
    }

    [TestMethod]
    public void DefaultProvider_CanSelfElevate_NormalExe_ReturnsTrue()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        Assert.IsTrue(ElevationUtilities.DefaultProvider.CanSelfElevate());
    }

    [TestMethod]
    public void DefaultProvider_CanSelfElevate_NotUserInteractive_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => false;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.CanSelfElevate());
    }

    [TestMethod]
    public void DefaultProvider_CanSelfElevate_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.CanSelfElevate());
    }

    [TestMethod]
    public void DefaultProvider_TryRunElevated_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.DefaultProvider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void DefaultProvider_TryRunElevated_ProcessExitsZero_ReturnsTrue()
    {
        ElevationUtilities._getProcessPathFunc = () => "C:/app/MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => new Process();
        ElevationUtilities._waitForExit = (_, _) => true;
        ElevationUtilities._getExitCode = _ => 0;
        Assert.IsTrue(ElevationUtilities.DefaultProvider.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }
}
