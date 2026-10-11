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
        var systemInfo = new SystemInfo
        {
            // Two 16GB DIMMs
            _queryMemoryCapacities = () => [16L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024]
        };

        Assert.AreEqual(32, systemInfo._getInstalledMemoryGb());
    }

    [TestMethod]
    public void ComputeInstalledMemoryGB_WithNoCapacities_ReturnsZero()
    {
        var systemInfo = new SystemInfo
        {
            _queryMemoryCapacities = () => []
        };

        Assert.AreEqual(0, systemInfo._getInstalledMemoryGb());
    }

    [TestMethod]
    public void ComputeInstalledMemoryGB_WhenQueryThrows_ReturnsZero()
    {
        var systemInfo = new SystemInfo
        {
            _queryMemoryCapacities = () => throw new InvalidOperationException("WMI failed")
        };

        Assert.AreEqual(0, systemInfo._getInstalledMemoryGb());
    }

    // --- ComputeDiskModel ---

    [TestMethod]
    public void ComputeDiskModel_WithModel_ReturnsModel()
    {
        var systemInfo = new SystemInfo
        {
            _queryPartitionIds = _ => ["Disk #0, Partition #0"],
            _queryDiskModelForPartition = _ => "Samsung SSD 990 PRO"
        };

        Assert.AreEqual("Samsung SSD 990 PRO", systemInfo._getDiskModel("C:\\"));
    }

    [TestMethod]
    public void ComputeDiskModel_NoPartitions_ReturnsUnknown()
    {
        var systemInfo = new SystemInfo
        {
            _queryPartitionIds = _ => []
        };

        Assert.AreEqual("Unknown", systemInfo._getDiskModel("C:\\"));
    }

    [TestMethod]
    public void ComputeDiskModel_PartitionWithNoDisk_ReturnsUnknown()
    {
        var systemInfo = new SystemInfo
        {
            _queryPartitionIds = _ => ["Disk #0, Partition #0"],
            _queryDiskModelForPartition = _ => null
        };

        Assert.AreEqual("Unknown", systemInfo._getDiskModel("C:\\"));
    }

    [TestMethod]
    public void ComputeDiskModel_WhenQueryThrows_ReturnsError()
    {
        var systemInfo = new SystemInfo
        {
            _queryPartitionIds = _ => throw new InvalidOperationException("WMI failed")
        };

        var result = systemInfo._getDiskModel("C:\\");
        Assert.IsTrue(result.StartsWith("Error:", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("WMI failed"));
    }

    [TestMethod]
    public void ComputeDiskModel_EmptyPath_FallsBackToC()
    {
        var queriedDrive = "";
        var systemInfo = new SystemInfo
        {
            _queryPartitionIds = drive =>
            {
                queriedDrive = drive;
                return [];
            }
        };

        systemInfo._getDiskModel("");
        Assert.AreEqual("C", queriedDrive);
    }

    [TestMethod]
    public void ComputeDiskModel_NullRoot_FallsBackToC()
    {
        var queriedDrive = "";
        var systemInfo = new SystemInfo
        {
            _queryPartitionIds = drive =>
            {
                queriedDrive = drive;
                return [];
            }
        };

        // Relative path: GetPathRoot returns ""
        systemInfo._getDiskModel("relative/path");
        Assert.AreEqual("C", queriedDrive);
    }
}
