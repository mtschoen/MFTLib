using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Benchmark;

sealed class ScenarioMetricAccumulator
{
    public List<double> WallClocks { get; } = [];
    public List<int> RecordCounts { get; } = [];
    public List<long> ManagedAllocations { get; } = [];
    public List<long> PeakWorkingSets { get; } = [];
    public List<long> PeakPrivateBytesList { get; } = [];
    public List<ulong> NativeCompactBytesList { get; } = [];
}

public partial class BenchmarkRunner
{
    int RunMeasure(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            _writeLineToConsole("Usage: Benchmark.exe measure <compat|bounded|broker-stream> <mftPath> [iterations]");
            return 1;
        }

        var scenario = arguments[0].ToLowerInvariant();
        var mftPath = arguments[1];
        var iterations = arguments.Length > 2 &&
                         int.TryParse(arguments[2], CultureInfo.InvariantCulture, out var parsedIterations) &&
                         parsedIterations > 0
            ? parsedIterations
            : 3;

        if (!_fileExists(mftPath))
        {
            _writeLineToConsole($"Error: MFT file not found: {mftPath}");
            return 1;
        }

        if (scenario != "compat" && scenario != "bounded" && scenario != "broker-stream")
        {
            _writeLineToConsole(
                $"Error: Unknown scenario '{scenario}'. Expected 'compat', 'bounded', or 'broker-stream'.");
            return 1;
        }

        return ExecuteMeasure(scenario, mftPath, iterations);
    }

    int ExecuteMeasure(string scenario, string mftPath, int iterations)
    {
        var output = new StringBuilder();

        void Log(string line = "")
        {
            _writeLineToConsole(line);
            output.AppendLine(line);
        }

        Log($"--- Scenario: {scenario} ---");

        var metrics = new ScenarioMetricAccumulator();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            ExecuteMeasureIteration(scenario, mftPath, iteration, iterations, output, metrics);
        }

        if (metrics.WallClocks.Count == 0)
        {
            Log("  All iterations failed - no results to report.");
            Log();
            return 1;
        }

        LogMeasureResults(metrics, Log);
        return 0;
    }

    void ExecuteMeasureIteration(
        string scenario, string mftPath, int iteration, int iterations, StringBuilder output,
        ScenarioMetricAccumulator metrics)
    {
        _writeToConsole($"  Iteration {iteration + 1}/{iterations}... ");
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAllocated = _getTotalAllocatedBytes();
            var stopwatch = Stopwatch.StartNew();

            var (recordsCount, nativeBytes) = scenario switch
            {
                "compat" => _parseCompat(mftPath),
                "bounded" => _parseBounded(mftPath, 4096),
                "broker-stream" => _parseBrokerStream(mftPath, 4096),
                _ => throw new InvalidOperationException($"Unknown scenario: {scenario}")
            };

            stopwatch.Stop();
            var endAllocated = _getTotalAllocatedBytes();
            var elapsedMs = _getStopwatchElapsedMs(stopwatch);
            var managedAllocated = Math.Max(0L, endAllocated - startAllocated);
            var peakWorkingSet = _getPeakWorkingSet64();
            var peakPrivateBytes = _getPeakPrivateBytes64();

            metrics.WallClocks.Add(elapsedMs);
            metrics.RecordCounts.Add(recordsCount);
            metrics.ManagedAllocations.Add(managedAllocated);
            metrics.PeakWorkingSets.Add(peakWorkingSet);
            metrics.PeakPrivateBytesList.Add(peakPrivateBytes);
            metrics.NativeCompactBytesList.Add(nativeBytes);

            var iterationLine = $"{elapsedMs:F0}ms ({recordsCount:N0} records)";
            _writeLineToConsole(iterationLine);
            output.Append(CultureInfo.InvariantCulture, $"  Iteration {iteration + 1}/{iterations}... {iterationLine}")
                .AppendLine();
        }
        catch (Exception exception)
        {
            var failLine = $"FAILED: {exception.GetType().Name}: {exception.Message}";
            _writeLineToConsole(failLine);
            output.Append(CultureInfo.InvariantCulture, $"  Iteration {iteration + 1}/{iterations}... {failLine}")
                .AppendLine();
        }
    }

    static void LogMeasureResults(ScenarioMetricAccumulator metrics, Action<string> log)
    {
        var successCount = metrics.WallClocks.Count;
        var medianRecords = metrics.RecordCounts.OrderBy(x => x).ElementAt(successCount / 2);
        var medianWall = metrics.WallClocks.OrderBy(x => x).ElementAt(successCount / 2);
        var medianManagedAllocated = metrics.ManagedAllocations.OrderBy(x => x).ElementAt(successCount / 2);
        var medianWorkingSet = metrics.PeakWorkingSets.OrderBy(x => x).ElementAt(successCount / 2);
        var medianPrivateBytes = metrics.PeakPrivateBytesList.OrderBy(x => x).ElementAt(successCount / 2);
        var medianNativeCompact = metrics.NativeCompactBytesList.OrderBy(x => x).ElementAt(successCount / 2);
        var throughput = medianWall > 0 ? medianRecords / (medianWall / 1000.0) : 0;

        log($"  Results (median of {successCount} successful iteration{(successCount == 1 ? "" : "s")}):");
        log($"    Records:              {medianRecords,14:N0}");
        log($"    Managed allocated:    {medianManagedAllocated,14:N0} bytes");
        log($"    Peak working set:     {medianWorkingSet,14:N0} bytes");
        log($"    Peak private bytes:   {medianPrivateBytes,14:N0} bytes");
        log($"    Native compact bytes: {medianNativeCompact,14:N0} bytes");
        log($"    Wall clock:           {medianWall,14:F1}ms");
        log($"    Throughput:           {throughput,14:N0} records/sec (wall clock)");
        log(string.Empty);
    }
}
