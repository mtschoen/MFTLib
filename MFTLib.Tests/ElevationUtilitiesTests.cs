using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class ElevationUtilitiesTests
{
    [TestMethod]
    public void IsElevated_ReturnsBool()
    {
        // Smoke test: should not throw, returns a valid boolean
        var result = ElevationUtilities.IsElevated();
        Assert.IsInstanceOfType(result, typeof(bool));
    }

    [TestMethod]
    public void GetProcessPath_ReturnsNonNull()
    {
        var path = ElevationUtilities.GetProcessPath();
        Assert.IsNotNull(path);
        Assert.IsTrue(path.Length > 0);
    }

    [TestMethod]
    public void EnsureElevated_WhenNotAdmin_LaunchesElevatedProcess()
    {
        if (ElevationUtilities.IsElevated())
            Assert.Inconclusive("Test must run non-elevated.");

        // This triggers a UAC prompt. Accept or decline — both paths return without throwing.
        // Decline exercises the Win32Exception (1223) catch; accept exercises the full launch path.
        // The return value depends on the UAC response and the elevated process exit code.
        var result = ElevationUtilities.EnsureElevated();
        Assert.IsInstanceOfType(result, typeof(bool));
    }
}
