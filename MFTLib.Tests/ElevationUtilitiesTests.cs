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
}
