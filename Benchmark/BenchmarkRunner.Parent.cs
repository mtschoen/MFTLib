using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Benchmark;

public partial class BenchmarkRunner
{
    public Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0].Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            return RunIndexAsync(arguments.AsSpan(1).ToArray());
        }

        return Task.FromResult(Run(arguments));
    }

    int RunParent(string[] arguments)
    {
        const ulong defaultRecordCount = 8_000_000;
        var recordCount = arguments.Length > 0 &&
                          ulong.TryParse(arguments[0], CultureInfo.InvariantCulture, out var parsedRecordCount)
            ? parsedRecordCount
            : defaultRecordCount;
        var iterations = arguments.Length > 1 &&
                         int.TryParse(arguments[1], CultureInfo.InvariantCulture, out var parsedIterations)
            ? parsedIterations
            : 3;

        var outputPath = GetOptionValue(arguments, "--out");
        var compareBaselinePath = GetOptionValue(arguments, "--compare-baseline");

        var mftPath = Path.Combine(AppContext.BaseDirectory, "synthetic.mft");
        var output = new StringBuilder();

        void Log(string line = "")
        {
            _writeLineToConsole(line);
            output.AppendLine(line);
        }

        LogHeaders(recordCount, iterations, Log);
        GenerateSyntheticFile(mftPath, recordCount, Log, output);

        var scenarios = new[] { "compat", "bounded", "broker-stream" };
        var childFailed = ExecuteChildScenarios(scenarios, mftPath, iterations, Log, output);

        _deleteFile(mftPath);
        Log("Synthetic MFT file cleaned up.");

        if (childFailed)
        {
            return 1;
        }

        return SaveAndCompareBaseline(output, outputPath, compareBaselinePath, Log);
    }

    // Reports are written and threshold-compared only when the caller asks for it. An earlier
    // revision did both unconditionally against tracked files, which made the exit code depend on
    // a file the same run had just overwritten and made any smoke-sized run fail its own gate.
    static string? GetOptionValue(string[] arguments, string optionName)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    void LogHeaders(ulong recordCount, int iterations, Action<string> log)
    {
        var gitSha = _getGitCommitHash();
        var peakWorkingSet = _getPeakWorkingSet64();
        var peakPrivateBytes = _getPeakPrivateBytes64();

        log($"Git: {gitSha}");
        log($"Benchmark arguments: {recordCount} {iterations}");
        log($"Peak working set: {peakWorkingSet}");
        log($"Peak private bytes: {peakPrivateBytes}");
        log(string.Empty);

        log("System Info");
        log("====================================");
        log($"  Build:       {_systemInfo._getBuildConfiguration()}");
        log(
            $"  OS:          {_systemInfo._getWmiValue("Win32_OperatingSystem", "Caption")} ({Environment.OSVersion.Version})");
        log($"  CPU:         {_systemInfo._getWmiValue("Win32_Processor", "Name")}");
        log($"  Threads:     {Environment.ProcessorCount}");
        log($"  RAM:         {_systemInfo._getInstalledMemoryGb()} GB");
        log($"  Disk:        {_systemInfo._getDiskModel(AppContext.BaseDirectory)}");
        log($"  .NET:        {RuntimeInformation.FrameworkDescription}");
        log($"  Date:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log(string.Empty);

        log("MFT Benchmark");
        log($"  Records: {recordCount:N0}");
        log($"  Iterations: {iterations}");
        log(string.Empty);
    }

    void GenerateSyntheticFile(string mftPath, ulong recordCount, Action<string> log, StringBuilder output)
    {
        _writeToConsole("Generating synthetic MFT... ");
        var generationStopwatch = Stopwatch.StartNew();
        _generateSynthetic(mftPath, recordCount, 262144);
        generationStopwatch.Stop();

        var fileInfo = _getFileInfo(mftPath);
        var generationLine =
            $"done in {generationStopwatch.Elapsed.TotalSeconds:F1}s ({fileInfo.Length / 1024.0 / 1024 / 1024:F2} GB)";
        _writeLineToConsole(generationLine);
        output.Append(CultureInfo.InvariantCulture, $"Generating synthetic MFT... {generationLine}").AppendLine();
        log(string.Empty);
    }

    bool ExecuteChildScenarios(
        string[] scenarios, string mftPath, int iterations, Action<string> log, StringBuilder output)
    {
        foreach (var scenario in scenarios)
        {
            var (exitCode, stdout, stderr) = _runChildProcess([
                "measure", scenario, mftPath, iterations.ToString(CultureInfo.InvariantCulture)
            ]);
            if (exitCode != 0)
            {
                log($"Error: Child process for scenario '{scenario}' exited with code {exitCode}.");
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    log(stderr.TrimEnd());
                }

                return true;
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                var trimmed = stdout.TrimEnd();
                _writeLineToConsole(trimmed);
                output.AppendLine(trimmed);
            }
        }

        return false;
    }

    int SaveAndCompareBaseline(StringBuilder output, string? outputPath, string? compareBaselinePath,
        Action<string> log)
    {
        if (outputPath == null)
        {
            log(string.Empty);
            log("Report not saved (pass --out <path> to write it).");
        }
        else
        {
            var resolvedOutputPath = Path.GetFullPath(outputPath);
            _writeAllText(resolvedOutputPath, output.ToString());
            log($"Report saved to {resolvedOutputPath}");
        }

        if (compareBaselinePath == null)
        {
            log("Thresholds not evaluated (pass --compare-baseline <path> to enforce them).");
            return 0;
        }

        var resolvedBaselinePath = Path.GetFullPath(compareBaselinePath);
        if (!_fileExists(resolvedBaselinePath))
        {
            log($"Error: Baseline before file not found: {resolvedBaselinePath}");
            return 1;
        }

        log(string.Empty);
        return RunCompare(resolvedBaselinePath, output.ToString(), log);
    }
}
