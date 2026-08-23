using System.Globalization;
using System.Management;

namespace Benchmark;

#pragma warning disable CA1416 // Validate platform compatibility - Benchmark is Windows-only

class SystemInfo
{
    internal Func<string> _getBuildConfiguration = DefaultGetBuildConfiguration;
    internal Func<string, string> _getDiskModel;
    internal Func<int> _getInstalledMemoryGb;
    internal Func<string, string, string> _getWmiValue = DefaultGetWmiValue;
    internal Func<string, string?> _queryDiskModelForPartition = DefaultQueryDiskModelForPartition;

    // Injectable WMI query functions used by the default implementations
    internal Func<IEnumerable<long>> _queryMemoryCapacities = DefaultQueryMemoryCapacities;
    internal Func<string, IEnumerable<string>> _queryPartitionIds = DefaultQueryPartitionIds;

    internal SystemInfo()
    {
        _getInstalledMemoryGb = ComputeInstalledMemoryGB;
        _getDiskModel = ComputeDiskModel;
    }

    // Coalesce a WMI property value (object, nullable ToString) to a trimmed
    // string. A null value yields "Unknown"; both branches are unit-tested.
    internal static string WmiString(object? value)
    {
        return (value?.ToString() ?? "Unknown").Trim();
    }

    internal static string DefaultGetWmiValue(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var managementObject in searcher.Get())
            {
                return WmiString(managementObject[property]);
            }
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

    int ComputeInstalledMemoryGB()
    {
        try
        {
            long total = 0;
            foreach (var capacity in _queryMemoryCapacities())
            {
                total += capacity;
            }

            if (total > 0)
            {
                return (int)(total / 1024 / 1024 / 1024);
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Warning: Failed to query RAM capacity: {exception.Message}");
        }

        return 0;
    }

    string ComputeDiskModel(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            var drive = string.IsNullOrEmpty(root) ? "C" : root[..1];

            foreach (var partitionId in _queryPartitionIds(drive))
            {
                var model = _queryDiskModelForPartition(partitionId);
                if (model != null)
                {
                    return model;
                }
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
        {
            yield return Convert.ToInt64(managementObject["Capacity"], CultureInfo.InvariantCulture);
        }
    }

    internal static IEnumerable<string> DefaultQueryPartitionIds(string drive)
    {
        using var partitionSearch = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
        foreach (var partition in partitionSearch.Get())
        {
            yield return WmiString(partition["DeviceID"]);
        }
    }

    internal static string? DefaultQueryDiskModelForPartition(string partitionId)
    {
        try
        {
            using var diskSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            // MoveNext() either returns true (valid partition always has a disk)
            // or throws ManagementException (invalid partition ID)
            using var enumerator = diskSearch.Get().GetEnumerator();
            enumerator.MoveNext();
            return WmiString(enumerator.Current["Model"]);
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
