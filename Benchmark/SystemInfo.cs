using System.Globalization;
using System.Management;

namespace Benchmark;

static class SystemInfo
{
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

    internal static (int Gigabytes, string? Warning) ComputeInstalledMemoryGB(IEnumerable<long> capacities)
    {
        try
        {
            long total = 0;
            foreach (var capacity in capacities)
            {
                total += capacity;
            }

            return (total > 0 ? (int)(total / 1024 / 1024 / 1024) : 0, null);
        }
        catch (Exception exception)
        {
            return (0, $"Warning: Failed to query RAM capacity: {exception.Message}");
        }
    }

    internal static string ComputeDiskModel(IEnumerable<string?> models)
    {
        try
        {
            foreach (var model in models)
            {
                if (model is not null)
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

    internal static string DriveForPath(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? "C" : root[..1];
    }

    internal static IEnumerable<string?> QueryDiskModels(string path)
    {
        foreach (var partition in DefaultQueryPartitionIds(DriveForPath(path)))
        {
            yield return DefaultQueryDiskModelForPartition(partition);
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
