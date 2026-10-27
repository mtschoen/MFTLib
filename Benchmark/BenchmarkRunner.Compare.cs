using System.Globalization;
using System.Text.RegularExpressions;

namespace Benchmark;

public partial class BenchmarkRunner
{
    // A scenario fails when its median or peak private bytes grows by more than this share of the baseline.
    const decimal MaximumGrowthPercentage = 25m;

    internal readonly record struct ScenarioMeasurements(
        decimal MedianMilliseconds, long PeakPrivateBytes, long PeakWorkingSetBytes, string MemoryMethod);

    int RunCompare(string[] arguments)
    {
        if (arguments.Length != 3)
        {
            _output.WriteLine("Usage: Benchmark.exe compare <before.txt> <after.txt> <scenario>");
            return 1;
        }

        var scenario = arguments[2];
        var before = ReadScenarioMeasurements(arguments[0], scenario, "Baseline before");
        var after = ReadScenarioMeasurements(arguments[1], scenario, "Benchmark after");
        if (before is not { } beforeMeasurements || after is not { } afterMeasurements)
        {
            return 1;
        }

        if (!string.Equals(beforeMeasurements.MemoryMethod, afterMeasurements.MemoryMethod, StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"Error: memory measurement methods differ for scenario '{scenario}': '{beforeMeasurements.MemoryMethod}' vs '{afterMeasurements.MemoryMethod}'.");
            return 1;
        }

        try
        {
            var timePassed = PrintComparison(
                scenario, "Median", beforeMeasurements.MedianMilliseconds, afterMeasurements.MedianMilliseconds, "ms");
            var memoryPassed = PrintComparison(
                scenario, "Peak private bytes", beforeMeasurements.PeakPrivateBytes, afterMeasurements.PeakPrivateBytes, "bytes");
            return timePassed && memoryPassed ? 0 : 1;
        }
        catch (OverflowException)
        {
            _output.WriteLine($"Error: invalid measurement range for scenario '{scenario}'.");
            return 1;
        }
    }

    internal static decimal Growth(decimal before, decimal after) => before > 0
        ? (after - before) / before * 100m
        : after > 0 ? decimal.MaxValue : 0m;

    bool PrintComparison(string scenario, string metric, decimal before, decimal after, string unit)
    {
        var growth = Growth(before, after);
        var passed = growth <= MaximumGrowthPercentage;
        var format = unit == "ms" ? "F3" : "F0";
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{scenario} {metric}: {before.ToString(format, CultureInfo.InvariantCulture)} {unit} -> {after.ToString(format, CultureInfo.InvariantCulture)} {unit} ({growth:F1}% growth, limit {MaximumGrowthPercentage:F1}%) {(passed ? "PASSED" : "FAILED")}"));
        return passed;
    }

    internal ScenarioMeasurements? ReadScenarioMeasurements(string path, string scenario, string label)
    {
        if (!File.Exists(path))
        {
            _output.WriteLine($"Error: {label} file not found: {path}");
            return null;
        }

        string reportText;
        try
        {
            reportText = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _output.WriteLine($"Error: {label} file could not be read: {exception.Message}");
            return null;
        }

        var block = ExtractScenarioBlock(reportText, scenario);
        if (block is null)
        {
            _output.WriteLine($"Error: {label} file has no median for scenario '{scenario}'.");
            return null;
        }

        var lines = block.Split('\n').Select(line => line.Trim()).ToList();
        string? Field(string prefix)
        {
            var values = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
                .Select(line => line[prefix.Length..].Trim()).ToList();
            return values.Count == 1 ? values[0] : null;
        }

        if (Field("Median:") is not { } medianField || !TryParseMedianMilliseconds(medianField, out var median))
        {
            _output.WriteLine($"Error: {label} file has no median for scenario '{scenario}'.");
            return null;
        }

        var privateCount = lines.Count(line => line.StartsWith("Peak private bytes:", StringComparison.Ordinal));
        if (privateCount == 0)
        {
            _output.WriteLine($"Error: {label} file has no peak private bytes for scenario '{scenario}'.");
            return null;
        }

        if (Field("Peak private bytes:") is not { } privateField ||
            !long.TryParse(privateField, NumberStyles.None, CultureInfo.InvariantCulture, out var peakPrivateBytes) ||
            peakPrivateBytes <= 0)
        {
            _output.WriteLine($"Error: {label} file has invalid peak private bytes for scenario '{scenario}'.");
            return null;
        }

        if (Field("Peak working set:") is not { } workingSetField ||
            !long.TryParse(workingSetField, NumberStyles.None, CultureInfo.InvariantCulture, out var peakWorkingSetBytes) ||
            peakWorkingSetBytes <= 0)
        {
            _output.WriteLine($"Error: {label} file has invalid peak working set for scenario '{scenario}'.");
            return null;
        }

        if (Field("Memory measurement:") is not { Length: > 0 } memoryMethod)
        {
            _output.WriteLine($"Error: {label} file has no memory measurement method for scenario '{scenario}'.");
            return null;
        }

        return new ScenarioMeasurements(median, peakPrivateBytes, peakWorkingSetBytes, memoryMethod);
    }

    static bool TryParseMedianMilliseconds(string field, out decimal median)
    {
        median = 0;
        var unitIndex = field.IndexOf(" ms", StringComparison.Ordinal);
        if (unitIndex <= 0)
        {
            return false;
        }

        var numberPart = field[..unitIndex];
        var suffix = field[(unitIndex + 3)..];
        if (suffix.Length > 0 &&
            !suffix.StartsWith("; Matches: ", StringComparison.Ordinal) &&
            !suffix.StartsWith("; Rows: ", StringComparison.Ordinal))
        {
            return false;
        }

        return decimal.TryParse(numberPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out median) &&
               median >= 0;
    }

    // The report's first block of that name; a cache directory with several drives repeats the blocks per drive.
    static string? ExtractScenarioBlock(string reportText, string scenarioName)
    {
        var match = Regex.Match(reportText,
            $@"(?:^|\r?\n)--- {Regex.Escape(scenarioName)} ---\r?\n([\s\S]*?)(?=\r?\n--- |\z)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
