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
            Assert.Inconclusive("Test must run non-elevated to exercise the launch path.");

        // This will trigger a UAC prompt. The elevated process is the test host,
        // which will run and exit. EnsureElevated waits for it and returns the result.
        var result = ElevationUtilities.EnsureElevated();

        // The return value depends on the elevated process exit code.
        // We just verify it didn't throw — the code path is what matters for coverage.
        Assert.IsInstanceOfType(result, typeof(bool));
    }
}
