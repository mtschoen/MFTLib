using System.Management;

namespace Benchmark;

#pragma warning disable CA1416 // Validate platform compatibility — Benchmark is Windows-only

internal class SystemInfo
{
    internal Func<string, string, string> GetWmiValue = DefaultGetWmiValue;
    internal Func<int> GetInstalledMemoryGB;
    internal Func<string, string> GetDiskModel;
    internal Func<string> GetBuildConfiguration = DefaultGetBuildConfiguration;

    // Injectable WMI query functions used by the default implementations
    internal Func<IEnumerable<long>> QueryMemoryCapacities = DefaultQueryMemoryCapacities;
    internal Func<string, IEnumerable<string>> QueryPartitionIds = DefaultQueryPartitionIds;
    internal Func<string, string?> QueryDiskModelForPartition = DefaultQueryDiskModelForPartition;

    internal SystemInfo()
    {
        GetInstalledMemoryGB = ComputeInstalledMemoryGB;
        GetDiskModel = ComputeDiskModel;
    }

    internal static string DefaultGetWmiValue(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var managementObject in searcher.Get())
                return managementObject[property].ToString()!.Trim();
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
        return "Unknown";
    }

    internal static string DefaultGetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    internal int ComputeInstalledMemoryGB()
    {
        try
        {
            long total = 0;
            foreach (var capacity in QueryMemoryCapacities())
                total += capacity;
            if (total > 0)
                return (int)(total / 1024 / 1024 / 1024);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Warning: Failed to query RAM capacity: {exception.Message}");
        }
        return 0;
    }

    internal string ComputeDiskModel(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            var drive = string.IsNullOrEmpty(root) ? "C" : root[..1];

            foreach (var partitionId in QueryPartitionIds(drive))
            {
                var model = QueryDiskModelForPartition(partitionId);
                if (model != null)
                    return model;
            }
            return "Unknown";
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    internal static IEnumerable<long> DefaultQueryMemoryCapacities()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
        foreach (var managementObject in searcher.Get())
            yield return Convert.ToInt64(managementObject["Capacity"]);
    }

    internal static IEnumerable<string> DefaultQueryPartitionIds(string drive)
    {
        using var partitionSearch = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
        foreach (var partition in partitionSearch.Get())
            yield return partition["DeviceID"].ToString()!;
    }

    internal static string? DefaultQueryDiskModelForPartition(string partitionId)
    {
        try
        {
            using var diskSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            // MoveNext() either returns true (valid partition always has a disk)
            // or throws ManagementException (invalid partition ID)
            var enumerator = diskSearch.Get().GetEnumerator();
            enumerator.MoveNext();
            return enumerator.Current["Model"].ToString()!.Trim();
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
