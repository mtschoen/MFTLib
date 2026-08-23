using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Benchmark;

public partial class BenchmarkRunner
{
    int RunCompare(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            _writeLineToConsole("Usage: Benchmark.exe compare <before.txt> <after.txt>");
            return 1;
        }

        return RunCompare(arguments[0], arguments[1], _writeLineToConsole, new StringBuilder());
    }

    int RunCompare(string beforePath, string afterPath, Action<string> log, StringBuilder output)
    {
        void LogLine(string line = "")
        {
            log(line);
            output.AppendLine(line);
        }

        if (!_fileExists(beforePath))
        {
            LogLine($"Error: Baseline before file not found: {beforePath}");
            return 1;
        }

        if (!_fileExists(afterPath))
        {
            LogLine($"Error: Benchmark after file not found: {afterPath}");
            return 1;
        }

        return CompareTexts(_readAllText(beforePath), _readAllText(afterPath), LogLine);
    }

    // Compares against the report text held in memory rather than re-reading a file the same run
    // just wrote, so the thresholds are evaluated on the measurements actually taken.
    int RunCompare(string beforePath, string afterText, Action<string> log)
    {
        return CompareTexts(_readAllText(beforePath), afterText, log);
    }

    static int CompareTexts(string beforeText, string afterText, Action<string> log)
    {
        if (!ValidateBeforeBaseline(beforeText, log, out var beforeGitSha, out var beforePeakPrivateBytes,
                out var beforeThroughput))
        {
            return 1;
        }

        var metrics = ExtractReportMetrics(afterText);
        if (metrics == null)
        {
            log("Error: Benchmark after file missing required scenario measurements.");
            return 1;
        }

        return EvaluateThresholds(beforeGitSha, beforePeakPrivateBytes, beforeThroughput, metrics, log);
    }

    static bool ValidateBeforeBaseline(
        string beforeText, Action<string> log, out string beforeGitSha, out long beforePeakPrivateBytes,
        out double beforeThroughput)
    {
        beforeGitSha = string.Empty;
        beforePeakPrivateBytes = 0;
        beforeThroughput = 0;

        var gitShaMatch = Regex.Match(beforeText, @"Git:\s*([0-9a-fA-F]{40})");
        if (!gitShaMatch.Success)
        {
            log("Error: Baseline before file validation failed: missing 40-character hex Git SHA.");
            return false;
        }

        beforeGitSha = gitShaMatch.Groups[1].Value;

        var beforePeakPrivateMatch = Regex.Match(beforeText, @"Peak private bytes:\s*([0-9,]+)");
        if (!beforePeakPrivateMatch.Success || !long.TryParse(beforePeakPrivateMatch.Groups[1].Value.Replace(",", ""),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out beforePeakPrivateBytes) ||
            beforePeakPrivateBytes <= 0)
        {
            log("Error: Baseline before file validation failed: missing or invalid Peak private bytes.");
            return false;
        }

        var beforeThroughputMatch =
            Regex.Match(beforeText, @"(?:Throughput:\s*|\s+)([0-9,]+)\s*records/sec\s*\(wall clock\)");
        if (!beforeThroughputMatch.Success)
        {
            beforeThroughputMatch = Regex.Match(beforeText, @"Throughput:\s*([0-9,]+)\s*records/sec");
        }

        if (!beforeThroughputMatch.Success || !double.TryParse(beforeThroughputMatch.Groups[1].Value.Replace(",", ""),
                NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out beforeThroughput) ||
            beforeThroughput <= 0)
        {
            log("Error: Baseline before file validation failed: missing or invalid Throughput.");
            return false;
        }

        return true;
    }

    static int EvaluateThresholds(
        string beforeGitSha, long beforePeakPrivateBytes, double beforeThroughput, ReportMetrics metrics,
        Action<string> log)
    {
        var throughputRegressionPercentage = beforeThroughput > 0
            ? (beforeThroughput - metrics.CompatThroughput) / beforeThroughput * 100.0
            : 0.0;
        var throughputPass = throughputRegressionPercentage <= 10.0;

        var privateBytesReductionPercentage = beforePeakPrivateBytes > 0
            ? (beforePeakPrivateBytes - metrics.CompatPeakPrivateBytes) / (double)beforePeakPrivateBytes * 100.0
            : 0.0;
        var privateBytesPass = privateBytesReductionPercentage >= 40.0;

        var boundedPass = metrics.BoundedPeakPrivateBytes <= metrics.CompatPeakPrivateBytes;
        var brokerPass = metrics.BrokerStreamPeakPrivateBytes <= metrics.CompatPeakPrivateBytes;
        var allPassed = throughputPass && privateBytesPass && boundedPass && brokerPass;

        log("Comparison Report");
        log("====================================");
        log($"  Baseline Git:                 {beforeGitSha}");
        log($"  Baseline throughput:          {beforeThroughput,14:N0} records/sec");
        log($"  Baseline peak private bytes:  {beforePeakPrivateBytes,14:N0} bytes");
        log(string.Empty);
        log($"  Compat throughput:            {metrics.CompatThroughput,14:N0} records/sec");
        log($"  Compat peak private bytes:    {metrics.CompatPeakPrivateBytes,14:N0} bytes");
        log($"  Bounded peak private bytes:   {metrics.BoundedPeakPrivateBytes,14:N0} bytes");
        log($"  Broker-stream peak private:   {metrics.BrokerStreamPeakPrivateBytes,14:N0} bytes");
        log(string.Empty);
        log(
            $"  Throughput regression:        {throughputRegressionPercentage,14:F1}% (limit: <= 10.0%) -> {(throughputPass ? "PASS" : "FAIL")}");
        log(
            $"  Peak private bytes reduction: {privateBytesReductionPercentage,14:F1}% (limit: >= 40.0%) -> {(privateBytesPass ? "PASS" : "FAIL")}");
        log($"  Bounded <= Compat peak:       {(boundedPass ? "PASS" : "FAIL")}");
        log($"  Broker-stream <= Compat peak: {(brokerPass ? "PASS" : "FAIL")}");
        log(string.Empty);
        log("Threshold: throughput regression <= 10.0%, peak private bytes reduction >= 40.0%");
        log($"Threshold check: {(allPassed ? "PASSED" : "FAILED")}");

        return allPassed ? 0 : 1;
    }

    static string? ExtractScenarioBlock(string reportText, string scenarioName)
    {
        var match = Regex.Match(reportText,
            $@"(?:^|\r?\n)--- (?:Scenario:\s*)?{scenarioName}\s*---(?:\r?\n)([\s\S]*?)(?=(?:^|\r?\n)--- |\z)");
        return match.Success ? match.Groups[1].Value : null;
    }

    static ReportMetrics? ExtractReportMetrics(string reportText)
    {
        var compatBlock = ExtractScenarioBlock(reportText, "compat");
        var boundedBlock = ExtractScenarioBlock(reportText, "bounded");
        var brokerStreamBlock = ExtractScenarioBlock(reportText, "broker-stream");

        if (compatBlock == null || boundedBlock == null || brokerStreamBlock == null)
        {
            return null;
        }

        var compatThroughputMatch = Regex.Match(compatBlock, @"Throughput:\s*([0-9,]+)");
        var compatPeakPrivateMatch = Regex.Match(compatBlock, @"Peak private bytes:\s*([0-9,]+)");
        var boundedPeakPrivateMatch = Regex.Match(boundedBlock, @"Peak private bytes:\s*([0-9,]+)");
        var brokerPeakPrivateMatch = Regex.Match(brokerStreamBlock, @"Peak private bytes:\s*([0-9,]+)");

        if (!compatThroughputMatch.Success || !compatPeakPrivateMatch.Success ||
            !boundedPeakPrivateMatch.Success || !brokerPeakPrivateMatch.Success)
        {
            return null;
        }

        if (!double.TryParse(compatThroughputMatch.Groups[1].Value.Replace(",", ""),
                NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture,
                out var compatThroughput))
        {
            return null;
        }

        if (!long.TryParse(compatPeakPrivateMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var compatPeakPrivate))
        {
            return null;
        }

        if (!long.TryParse(boundedPeakPrivateMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var boundedPeakPrivate))
        {
            return null;
        }

        if (!long.TryParse(brokerPeakPrivateMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var brokerPeakPrivate))
        {
            return null;
        }

        return new ReportMetrics(compatThroughput, compatPeakPrivate, boundedPeakPrivate, brokerPeakPrivate);
    }
}
