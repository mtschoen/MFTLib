using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using MFTLib;

const ulong defaultRecordCount = 8_000_000;
var recordCount = args.Length > 0 && ulong.TryParse(args[0], out var rc) ? rc : defaultRecordCount;
var iterations = args.Length > 1 && int.TryParse(args[1], out var it) ? it : 3;

var mftPath = Path.Combine(AppContext.BaseDirectory, "synthetic.mft");
var output = new StringBuilder();

void Log(string line = "")
{
    Console.WriteLine(line);
    output.AppendLine(line);
}

// System info
Log("System Info");
Log("====================================");
Log($"  Build:       {GetBuildConfiguration()}");
Log($"  OS:          {GetWmiValue("Win32_OperatingSystem", "Caption")} ({Environment.OSVersion.Version})");
Log($"  CPU:         {GetWmiValue("Win32_Processor", "Name")}");
Log($"  Threads:     {Environment.ProcessorCount}");
Log($"  RAM:         {GetInstalledMemoryGB()} GB");
Log($"  Disk:        {GetDiskModel(AppContext.BaseDirectory)}");
Log($"  .NET:        {RuntimeInformation.FrameworkDescription}");
Log($"  Date:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Log();

Log($"MFT Benchmark");
Log($"  Records: {recordCount:N0}");
Log($"  Iterations: {iterations}");
Log();

// Generate synthetic MFT
Console.Write("Generating synthetic MFT... ");
var genSw = Stopwatch.StartNew();
MftVolume.GenerateSyntheticMFT(mftPath, recordCount);
genSw.Stop();

var fileInfo = new FileInfo(mftPath);
var genLine = $"done in {genSw.Elapsed.TotalSeconds:F1}s ({fileInfo.Length / 1024.0 / 1024 / 1024:F2} GB)";
Console.WriteLine(genLine);
output.AppendLine($"Generating synthetic MFT... {genLine}");
Log();

// Benchmark scenarios
var scenarios = new (string Name, string? Filter, uint MatchFlags)[]
{
    ("Unfiltered (all records)", null, 0),
    ("Filtered: exact \".git\"", ".git", 1),
    ("Filtered: contains \"config\"", "config", 2),
    ("Filtered: exact \".git\" + paths", ".git", 1 | 4),
};

foreach (var (scenarioName, filter, matchFlags) in scenarios)
{
    Log($"--- {scenarioName} ---");

    var allTimings = new List<MftParseTimings>();
    var allWallClocks = new List<double>();
    var recordCounts = new List<int>();

    for (int i = 0; i < iterations; i++)
    {
        Console.Write($"  Iteration {i + 1}/{iterations}... ");
        var sw = Stopwatch.StartNew();
        var records = MftVolume.ParseMFTFromFile(mftPath, filter, matchFlags, out var timings);
        sw.Stop();

        allTimings.Add(timings);
        allWallClocks.Add(sw.Elapsed.TotalMilliseconds);
        recordCounts.Add(records.Length);

        var iterLine = $"{sw.Elapsed.TotalMilliseconds:F0}ms ({records.Length:N0} records)";
        Console.WriteLine(iterLine);
        output.AppendLine($"  Iteration {i + 1}/{iterations}... {iterLine}");
    }

    var sorted = allWallClocks.OrderBy(x => x).ToList();
    var medianWall = sorted[sorted.Count / 2];
    var medianIdx = allWallClocks.IndexOf(medianWall);
    var medianTimings = allTimings[medianIdx];
    var medianRecords = recordCounts[medianIdx];

    Log($"  Results (median):");
    Log($"    Records:      {medianRecords,12:N0}");
    Log($"    Wall clock:   {medianWall,12:F1}ms");
    Log($"    Native total: {medianTimings.NativeTotalMs,12:F1}ms");
    Log($"      I/O:        {medianTimings.NativeIoMs,12:F1}ms  ({medianTimings.NativeIoMs / medianWall * 100:F1}%)");
    Log($"      Fixup:      {medianTimings.NativeFixupMs,12:F1}ms  ({medianTimings.NativeFixupMs / medianWall * 100:F1}%)");
    Log($"      Parse:      {medianTimings.NativeParseMs,12:F1}ms  ({medianTimings.NativeParseMs / medianWall * 100:F1}%)");
    Log($"    Marshal:      {medianTimings.MarshalMs,12:F1}ms  ({medianTimings.MarshalMs / medianWall * 100:F1}%)");
    Log($"    Throughput:   {recordCount / (medianWall / 1000.0),12:N0} records/sec");
    Log();
}

// Cleanup
File.Delete(mftPath);
Log("Synthetic MFT file cleaned up.");

// Save baseline
var baselinePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Benchmark", "baseline.txt"));
File.WriteAllText(baselinePath, output.ToString());
Log($"Baseline saved to {baselinePath}");

static string GetWmiValue(string wmiClass, string property)
{
    try
    {
        using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
        foreach (var obj in searcher.Get())
            return obj[property]?.ToString()?.Trim() ?? "Unknown";
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
    return "Unknown";
}

static string GetBuildConfiguration()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}

static int GetInstalledMemoryGB()
{
    try
    {
        // Sum installed DIMM capacities for the real total (not OS-usable)
        using var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
        long total = 0;
        foreach (var obj in searcher.Get())
            if (obj["Capacity"] is { } val)
                total += Convert.ToInt64(val);
        if (total > 0)
            return (int)(total / 1024 / 1024 / 1024);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to query RAM capacity: {ex.Message}");
    }
    return 0;
}

static string GetDiskModel(string path)
{
    try
    {
        var drive = Path.GetPathRoot(path)?[..1] ?? "C";
        // Map drive letter -> physical disk model via WMI associations
        using var partSearch = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
        foreach (var part in partSearch.Get())
        {
            using var diskSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            foreach (var disk in diskSearch.Get())
                return disk["Model"]?.ToString()?.Trim() ?? "Unknown";
        }
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
    return "Unknown";
}
