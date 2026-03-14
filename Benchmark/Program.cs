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

// Run benchmark iterations
var allTimings = new List<MftParseTimings>();
var allWallClocks = new List<double>();

for (int i = 0; i < iterations; i++)
{
    Console.Write($"Iteration {i + 1}/{iterations}... ");
    var sw = Stopwatch.StartNew();
    MftVolume.ParseMFTFromFile(mftPath, out var timings);
    sw.Stop();

    allTimings.Add(timings);
    allWallClocks.Add(sw.Elapsed.TotalMilliseconds);

    var iterLine = $"{sw.Elapsed.TotalMilliseconds:F0}ms";
    Console.WriteLine(iterLine);
    output.AppendLine($"Iteration {i + 1}/{iterations}... {iterLine}");
}

Log();
Log("Results (median of all iterations):");
Log("====================================");

var sorted = allWallClocks.OrderBy(x => x).ToList();
var medianWall = sorted[sorted.Count / 2];
var medianTimings = allTimings[allWallClocks.IndexOf(medianWall)];

Log($"  Wall clock:     {medianWall,9:F1}ms");
Log($"  Native total:   {medianTimings.NativeTotalMs,9:F1}ms");
Log($"    I/O:          {medianTimings.NativeIoMs,9:F1}ms  ({medianTimings.NativeIoMs / medianWall * 100:F1}%)");
Log($"    Fixup:        {medianTimings.NativeFixupMs,9:F1}ms  ({medianTimings.NativeFixupMs / medianWall * 100:F1}%)");
Log($"    Parse:        {medianTimings.NativeParseMs,9:F1}ms  ({medianTimings.NativeParseMs / medianWall * 100:F1}%)");
Log($"  Marshal:        {medianTimings.MarshalMs,9:F1}ms  ({medianTimings.MarshalMs / medianWall * 100:F1}%)");
Log($"  Throughput:     {recordCount / (medianWall / 1000.0):N0} records/sec");

// Cleanup
File.Delete(mftPath);
Log();
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
    catch { }
    return "Unknown";
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
    catch { }
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
    catch { }
    return "Unknown";
}
