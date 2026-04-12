using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MFTLib;

namespace Benchmark;

#pragma warning disable CA1416 // Validate platform compatibility — Benchmark is Windows-only

internal class BenchmarkRunner
{
    internal SystemInfo SystemInfo = new();
    internal Action<string, ulong, uint> GenerateSynthetic = MftVolume.GenerateSyntheticMFT;
    internal Func<string, string?, MatchFlags, (MftRecord[] Records, MftParseTimings Timings)> ParseFromFile =
        (path, filter, flags) =>
        {
            var records = MftVolume.ParseMFTFromFile(path, filter, flags, out var timings);
            return (records, timings);
        };
    internal Action<string> DeleteFile = File.Delete;
    internal Func<string, FileInfo> GetFileInfo = path => new FileInfo(path);
    internal Action<string, string> WriteAllText = File.WriteAllText;
    internal Action<string> WriteLineToConsole = Console.WriteLine;
    internal Action<string> WriteToConsole = value => Console.Write(value);

    internal int Run(string[] arguments)
    {
        const ulong defaultRecordCount = 8_000_000;
        var recordCount = arguments.Length > 0 && ulong.TryParse(arguments[0], out var parsedRecordCount) ? parsedRecordCount : defaultRecordCount;
        var iterations = arguments.Length > 1 && int.TryParse(arguments[1], out var parsedIterations) ? parsedIterations : 3;

        var mftPath = Path.Combine(AppContext.BaseDirectory, "synthetic.mft");
        var output = new StringBuilder();

        void Log(string line = "")
        {
            WriteLineToConsole(line);
            output.AppendLine(line);
        }

        // System info
        Log("System Info");
        Log("====================================");
        Log($"  Build:       {SystemInfo.GetBuildConfiguration()}");
        Log($"  OS:          {SystemInfo.GetWmiValue("Win32_OperatingSystem", "Caption")} ({Environment.OSVersion.Version})");
        Log($"  CPU:         {SystemInfo.GetWmiValue("Win32_Processor", "Name")}");
        Log($"  Threads:     {Environment.ProcessorCount}");
        Log($"  RAM:         {SystemInfo.GetInstalledMemoryGB()} GB");
        Log($"  Disk:        {SystemInfo.GetDiskModel(AppContext.BaseDirectory)}");
        Log($"  .NET:        {RuntimeInformation.FrameworkDescription}");
        Log($"  Date:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log();

        Log("MFT Benchmark");
        Log($"  Records: {recordCount:N0}");
        Log($"  Iterations: {iterations}");
        Log();

        // Generate synthetic MFT
        WriteToConsole("Generating synthetic MFT... ");
        var generationStopwatch = Stopwatch.StartNew();
        GenerateSynthetic(mftPath, recordCount, 262144);
        generationStopwatch.Stop();

        var fileInfo = GetFileInfo(mftPath);
        var generationLine = $"done in {generationStopwatch.Elapsed.TotalSeconds:F1}s ({fileInfo.Length / 1024.0 / 1024 / 1024:F2} GB)";
        WriteLineToConsole(generationLine);
        output.AppendLine($"Generating synthetic MFT... {generationLine}");
        Log();

        // Benchmark scenarios
        var scenarios = new (string Name, string? Filter, MatchFlags MatchFlags)[]
        {
            ("Unfiltered (all records)", null, MatchFlags.None),
            ("Filtered: exact \".git\"", ".git", MatchFlags.ExactMatch),
            ("Filtered: contains \"config\"", "config", MatchFlags.Contains),
            ("Filtered: exact \".git\" + paths", ".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths),
        };

        foreach (var (scenarioName, filter, matchFlags) in scenarios)
        {
            RunScenario(scenarioName, filter, matchFlags, mftPath, iterations, recordCount, Log, output);
        }

        // Cleanup
        DeleteFile(mftPath);
        Log("Synthetic MFT file cleaned up.");

        // Save baseline
        var baselinePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Benchmark", "baseline.txt"));
        WriteAllText(baselinePath, output.ToString());
        Log($"Baseline saved to {baselinePath}");

        return 0;
    }

    internal void RunScenario(string scenarioName, string? filter, MatchFlags matchFlags,
        string mftPath, int iterations, ulong recordCount, Action<string> log, StringBuilder output)
    {
        log($"--- {scenarioName} ---");

        var allTimings = new List<MftParseTimings>();
        var allWallClocks = new List<double>();
        var recordCounts = new List<int>();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            WriteToConsole($"  Iteration {iteration + 1}/{iterations}... ");
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var (records, timings) = ParseFromFile(mftPath, filter, matchFlags);
                stopwatch.Stop();

                allTimings.Add(timings);
                allWallClocks.Add(stopwatch.Elapsed.TotalMilliseconds);
                recordCounts.Add(records.Length);

                var iterationLine = $"{stopwatch.Elapsed.TotalMilliseconds:F0}ms ({records.Length:N0} records)";
                WriteLineToConsole(iterationLine);
                output.AppendLine($"  Iteration {iteration + 1}/{iterations}... {iterationLine}");
            }
            catch (Exception exception)
            {
                var failLine = $"FAILED: {exception.GetType().Name}: {exception.Message}";
                WriteLineToConsole(failLine);
                output.AppendLine($"  Iteration {iteration + 1}/{iterations}... {failLine}");
            }
        }

        if (allWallClocks.Count == 0)
        {
            log("  All iterations failed — no results to report.");
            log(string.Empty);
            return;
        }

        var medianRecords = recordCounts.OrderBy(x => x).ElementAt(recordCounts.Count / 2);
        var medianIo = allTimings.Select(t => t.NativeIoMs).OrderBy(x => x).ElementAt(allTimings.Count / 2);
        var successCount = allTimings.Count;
        var medianFixup = allTimings.Select(t => t.NativeFixupMs).OrderBy(x => x).ElementAt(successCount / 2);
        var medianParse = allTimings.Select(t => t.NativeParseMs).OrderBy(x => x).ElementAt(successCount / 2);
        var medianMarshal = allTimings.Select(t => t.MarshalMs).OrderBy(x => x).ElementAt(successCount / 2);
        var medianWall = allWallClocks.OrderBy(x => x).ElementAt(successCount / 2);
        var computeMs = medianFixup + medianParse + medianMarshal;

        log($"  Results (median of {successCount} successful iteration{(successCount == 1 ? "" : "s")}):");
        log($"    Records:      {medianRecords,12:N0}");
        log($"    I/O:          {medianIo,12:F1}ms");
        log($"    Fixup:        {medianFixup,12:F1}ms");
        log($"    Parse:        {medianParse,12:F1}ms");
        log($"    Marshal:      {medianMarshal,12:F1}ms");
        log($"    Compute:      {computeMs,12:F1}ms  (fixup + parse + marshal)");
        log($"    Wall clock:   {medianWall,12:F1}ms");
        log($"    Throughput:   {recordCount / (computeMs / 1000.0),12:N0} records/sec (compute)");
        log($"                  {recordCount / (medianWall / 1000.0),12:N0} records/sec (wall clock)");
        log(string.Empty);
    }
}
