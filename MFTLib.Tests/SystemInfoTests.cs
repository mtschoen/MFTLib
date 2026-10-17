using System.Runtime.Versioning;
using Benchmark;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public class SystemInfoTests
{
    // --- DefaultGetBuildConfiguration ---

    [TestMethod]
    public void DefaultGetBuildConfiguration_ReturnsReleaseOrDebug()
    {
        var result = SystemInfo.DefaultGetBuildConfiguration();
        Assert.IsTrue(result is "Release" or "Debug");
    }

    [TestMethod]
    public void WmiString_NullValue_ReturnsUnknown()
    {
        Assert.AreEqual("Unknown", SystemInfo.WmiString(null));
    }

    [TestMethod]
    public void WmiString_TrimsValue()
    {
        Assert.AreEqual("disk", SystemInfo.WmiString("  disk  "));
    }

    // --- ComputeInstalledMemoryGB ---

    [TestMethod]
    public void ComputeInstalledMemoryGB_WithCapacities_ReturnsGB()
    {
        Assert.AreEqual((32, (string?)null), SystemInfo.ComputeInstalledMemoryGB(
            [16L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024]));
    }

    [TestMethod]
    public void ComputeInstalledMemoryGB_WithNoCapacities_ReturnsZero()
    {
        Assert.AreEqual((0, (string?)null), SystemInfo.ComputeInstalledMemoryGB([]));
    }

    [TestMethod]
    public void ComputeInstalledMemoryGB_WhenQueryThrows_ReturnsZero()
    {
        static IEnumerable<long> BrokenCapacities()
        {
            yield return 1024;
            throw new InvalidOperationException("WMI failed");
        }
        var memory = SystemInfo.ComputeInstalledMemoryGB(BrokenCapacities());
        Assert.AreEqual(0, memory.Gigabytes);
        Assert.AreEqual("Warning: Failed to query RAM capacity: WMI failed", memory.Warning);
    }

    // --- ComputeDiskModel ---

    [TestMethod]
    public void ComputeDiskModel_WithModel_ReturnsModel()
    {
        Assert.AreEqual("Samsung SSD 990 PRO", SystemInfo.ComputeDiskModel([null, "Samsung SSD 990 PRO"]));
    }

    [TestMethod]
    public void ComputeDiskModel_NoPartitions_ReturnsUnknown()
    {
        Assert.AreEqual("Unknown", SystemInfo.ComputeDiskModel([]));
    }

    [TestMethod]
    public void ComputeDiskModel_PartitionWithNoDisk_ReturnsUnknown()
    {
        Assert.AreEqual("Unknown", SystemInfo.ComputeDiskModel([null]));
    }

    [TestMethod]
    public void ComputeDiskModel_WhenQueryThrows_ReturnsError()
    {
        static IEnumerable<string?> BrokenModels()
        {
            yield return null;
            throw new InvalidOperationException("WMI failed");
        }
        Assert.AreEqual("Error: WMI failed", SystemInfo.ComputeDiskModel(BrokenModels()));
    }

    [TestMethod]
    public void ComputeDiskModel_EmptyPath_FallsBackToC()
    {
        Assert.AreEqual("C", SystemInfo.DriveForPath(""));
    }

    [TestMethod]
    public void ComputeDiskModel_NullRoot_FallsBackToC()
    {
        Assert.AreEqual("C", SystemInfo.DriveForPath("relative/path"));
    }
}
