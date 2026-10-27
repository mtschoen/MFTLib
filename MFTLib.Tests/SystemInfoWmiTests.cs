using System.Management;
using System.Runtime.Versioning;
using Benchmark;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The real WMI queries behind <see cref="SystemInfo" />. Windows only: System.Management throws
///     <see cref="PlatformNotSupportedException" /> elsewhere, so MFTLib.Tests.csproj leaves this file out of the Linux build.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public class SystemInfoWmiTests
{
    // --- DefaultGetWmiValue ---

    [TestMethod]
    public void DefaultGetWmiValue_WithValidClass_ReturnsNonEmptyString()
    {
        var result = SystemInfo.DefaultGetWmiValue("Win32_OperatingSystem", "Caption");
        Assert.IsNotNull(result);
        Assert.IsGreaterThan(0, result.Length);
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

    // --- Real WMI calls (integration) ---

    [TestMethod]
    public void DefaultQueryMemoryCapacities_ReturnsPositiveValues()
    {
        var capacities = SystemInfo.DefaultQueryMemoryCapacities().ToList();
        Assert.IsNotEmpty(capacities);
        Assert.IsTrue(capacities.All(capacity => capacity > 0));
    }

    [TestMethod]
    public void DefaultQueryPartitionIds_WithValidRoot_ReturnsPartitions()
    {
        var partitions = SystemInfo.DefaultQueryPartitionIds("C").ToList();
        Assert.IsNotEmpty(partitions);
    }

    [TestMethod]
    public void DefaultQueryPartitionIds_WithInvalidRoot_ReturnsEmptyOrThrows()
    {
        try
        {
            var partitions = SystemInfo.DefaultQueryPartitionIds("Z").ToList();
            Assert.IsEmpty(partitions);
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
        Assert.IsNotEmpty(partitions);

        var model = SystemInfo.DefaultQueryDiskModelForPartition(partitions[0]);
        Assert.IsNotNull(model);
        Assert.IsGreaterThan(0, model.Length);
    }

    [TestMethod]
    public void DefaultQueryDiskModelForPartition_WithInvalidPartition_ReturnsNull()
    {
        var model = SystemInfo.DefaultQueryDiskModelForPartition("FakePartition_999");
        Assert.IsNull(model);
    }
}
