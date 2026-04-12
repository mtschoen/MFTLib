using Microsoft.VisualStudio.TestTools.UnitTesting;
using Benchmark;

namespace MFTLib.Tests;

[TestClass]
public class SystemInfoTests
{
    // --- Func field swapping ---

    [TestMethod]
    public void GetWmiValue_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo();
        systemInfo.GetWmiValue = (wmiClass, property) => $"Mock:{wmiClass}.{property}";

        var result = systemInfo.GetWmiValue("Win32_OperatingSystem", "Caption");
        Assert.AreEqual("Mock:Win32_OperatingSystem.Caption", result);
    }

    [TestMethod]
    public void GetInstalledMemoryGB_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo();
        systemInfo.GetInstalledMemoryGB = () => 64;

        Assert.AreEqual(64, systemInfo.GetInstalledMemoryGB());
    }

    [TestMethod]
    public void GetDiskModel_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo();
        systemInfo.GetDiskModel = _ => "MockDisk";

        Assert.AreEqual("MockDisk", systemInfo.GetDiskModel("C:\\"));
    }

    [TestMethod]
    public void GetBuildConfiguration_FuncIsSwappable()
    {
        var systemInfo = new SystemInfo();
        systemInfo.GetBuildConfiguration = () => "TestConfig";

        Assert.AreEqual("TestConfig", systemInfo.GetBuildConfiguration());
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
        Assert.IsTrue(result.StartsWith("Error:"));
    }

    // --- ComputeInstalledMemoryGB ---

    [TestMethod]
    public void ComputeInstalledMemoryGB_WithCapacities_ReturnsGB()
    {
        var systemInfo = new SystemInfo();
        // Two 16GB DIMMs
        systemInfo.QueryMemoryCapacities = () => [16L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024];

        Assert.AreEqual(32, systemInfo.GetInstalledMemoryGB());
    }

    [TestMethod]
    public void ComputeInstalledMemoryGB_WithNoCapacities_ReturnsZero()
    {
        var systemInfo = new SystemInfo();
        systemInfo.QueryMemoryCapacities = () => [];

        Assert.AreEqual(0, systemInfo.GetInstalledMemoryGB());
    }

    [TestMethod]
    public void ComputeInstalledMemoryGB_WhenQueryThrows_ReturnsZero()
    {
        var systemInfo = new SystemInfo();
        systemInfo.QueryMemoryCapacities = () => throw new InvalidOperationException("WMI failed");

        Assert.AreEqual(0, systemInfo.GetInstalledMemoryGB());
    }

    // --- ComputeDiskModel ---

    [TestMethod]
    public void ComputeDiskModel_WithModel_ReturnsModel()
    {
        var systemInfo = new SystemInfo();
        systemInfo.QueryPartitionIds = _ => ["Disk #0, Partition #0"];
        systemInfo.QueryDiskModelForPartition = _ => "Samsung SSD 990 PRO";

        Assert.AreEqual("Samsung SSD 990 PRO", systemInfo.GetDiskModel("C:\\"));
    }

    [TestMethod]
    public void ComputeDiskModel_NoPartitions_ReturnsUnknown()
    {
        var systemInfo = new SystemInfo();
        systemInfo.QueryPartitionIds = _ => [];

        Assert.AreEqual("Unknown", systemInfo.GetDiskModel("C:\\"));
    }

    [TestMethod]
    public void ComputeDiskModel_PartitionWithNoDisk_ReturnsUnknown()
    {
        var systemInfo = new SystemInfo();
        systemInfo.QueryPartitionIds = _ => ["Disk #0, Partition #0"];
        systemInfo.QueryDiskModelForPartition = _ => null;

        Assert.AreEqual("Unknown", systemInfo.GetDiskModel("C:\\"));
    }

    [TestMethod]
    public void ComputeDiskModel_WhenQueryThrows_ReturnsError()
    {
        var systemInfo = new SystemInfo();
        systemInfo.QueryPartitionIds = _ => throw new InvalidOperationException("WMI failed");

        var result = systemInfo.GetDiskModel("C:\\");
        Assert.IsTrue(result.StartsWith("Error:"));
        Assert.IsTrue(result.Contains("WMI failed"));
    }

    [TestMethod]
    public void ComputeDiskModel_EmptyPath_FallsBackToC()
    {
        var queriedDrive = "";
        var systemInfo = new SystemInfo();
        systemInfo.QueryPartitionIds = drive => { queriedDrive = drive; return []; };

        systemInfo.GetDiskModel("");
        Assert.AreEqual("C", queriedDrive);
    }

    [TestMethod]
    public void ComputeDiskModel_NullRoot_FallsBackToC()
    {
        var queriedDrive = "";
        var systemInfo = new SystemInfo();
        systemInfo.QueryPartitionIds = drive => { queriedDrive = drive; return []; };

        // Relative path — GetPathRoot returns ""
        systemInfo.GetDiskModel("relative/path");
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
        catch (System.Management.ManagementException)
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
