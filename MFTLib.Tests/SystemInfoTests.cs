using System.Management;
using Benchmark;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class SystemInfoTests
{
    // --- Func field swapping ---

    [TestMethod]
    public void GetWmiValue_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo
        {
            _getWmiValue = (wmiClass, property) => $"Mock:{wmiClass}.{property}"
        };

        var result = systemInfo._getWmiValue("Win32_OperatingSystem", "Caption");
        Assert.AreEqual("Mock:Win32_OperatingSystem.Caption", result);
    }

    [TestMethod]
    public void GetInstalledMemoryGB_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo
        {
            _getInstalledMemoryGb = () => 64
        };

        Assert.AreEqual(64, systemInfo._getInstalledMemoryGb());
    }

    [TestMethod]
    public void GetDiskModel_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo
        {
            _getDiskModel = _ => "MockDisk"
        };

        Assert.AreEqual("MockDisk", systemInfo._getDiskModel("C:\\"));
    }

    [TestMethod]
    public void GetBuildConfiguration_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo
        {
            _getBuildConfiguration = () => "TestConfig"
        };

        Assert.AreEqual("TestConfig", systemInfo._getBuildConfiguration());
    }

    // --- DefaultGetBuildConfiguration ---

    [TestMethod]
    public void DefaultGetBuildConfiguration_ReturnsReleaseOrDebug()
    {
        var result = SystemInfo.DefaultGetBuildConfiguration();
        Assert.IsTrue(result is "Release" or "Debug");
    }

    // --- DefaultGetWmiValue ---

    [TestMethod]
    public void DefaultGetWmiValue_WithValidClass_ReturnsNonEmptyString()
    {
        var result = SystemInfo.DefaultGetWmiValue("Win32_OperatingSystem", "Caption");
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Length > 0);
    }

    [TestMethod]
    public void DefaultGetWmiValue_WithEmptyResultSet_ReturnsUnknown()
    {
        var result = SystemInfo.DefaultGetWmiValue("Win32_Process WHERE ProcessId = 99999999", "Name");
        Assert.AreEqual("Unknown", result);
    }

    [TestMethod]
    public void DefaultGetWmiValue_WithInvalidQuery_ReturnsError()
    {
        var result = SystemInfo.DefaultGetWmiValue("NonExistentWmiClass_XYZ_12345", "Property");
        Assert.IsTrue(result.StartsWith("Error:", StringComparison.Ordinal));
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

        // Relative path — GetPathRoot returns ""
        systemInfo._getDiskModel("relative/path");
        Assert.AreEqual("C", queriedDrive);
    }

    // --- Real WMI calls (integration) ---

    [TestMethod]
    public void DefaultQueryMemoryCapacities_ReturnsPositiveValues()
    {
        var capacities = SystemInfo.DefaultQueryMemoryCapacities().ToList();
        Assert.IsTrue(capacities.Count > 0);
        Assert.IsTrue(capacities.All(capacity => capacity > 0));
    }

    [TestMethod]
    public void DefaultQueryPartitionIds_WithValidDrive_ReturnsPartitions()
    {
        var partitions = SystemInfo.DefaultQueryPartitionIds("C").ToList();
        Assert.IsTrue(partitions.Count > 0);
    }

    [TestMethod]
    public void DefaultQueryPartitionIds_WithInvalidDrive_ReturnsEmptyOrThrows()
    {
        try
        {
            var partitions = SystemInfo.DefaultQueryPartitionIds("Z").ToList();
            Assert.AreEqual(0, partitions.Count);
        }
        catch (ManagementException)
        {
            // WMI throws "Not found" under elevated context for invalid drives
        }
    }

    [TestMethod]
    public void DefaultQueryDiskModelForPartition_WithValidPartition_ReturnsModel()
    {
        var partitions = SystemInfo.DefaultQueryPartitionIds("C").ToList();
        Assert.IsTrue(partitions.Count > 0);

        var model = SystemInfo.DefaultQueryDiskModelForPartition(partitions[0]);
        Assert.IsNotNull(model);
        Assert.IsTrue(model.Length > 0);
    }

    [TestMethod]
    public void DefaultQueryDiskModelForPartition_WithInvalidPartition_ReturnsNull()
    {
        var model = SystemInfo.DefaultQueryDiskModelForPartition("FakePartition_999");
        Assert.IsNull(model);
    }
}
