using System.Globalization;
using System.Text.RegularExpressions;

namespace Benchmark;

public partial class BenchmarkRunner
{
    // A scenario fails when its median grows by more than this share of the baseline median.
    const decimal MaximumGrowthPercentage = 25m;

    int RunCompare(string[] arguments)
    {
        if (arguments.Length != 3)
        {
            _output.WriteLine("Usage: Benchmark.exe compare <before.txt> <after.txt> <scenario>");
            return 1;
        }

        var scenario = arguments[2];
        var before = ReadScenarioMedian(arguments[0], scenario, "Baseline before");
        var after = ReadScenarioMedian(arguments[1], scenario, "Benchmark after");
        if (before is not { } beforeMedian || after is not { } afterMedian)
        {
            return 1;
        }

        var growthPercentage = beforeMedian > 0
            ? (afterMedian - beforeMedian) / beforeMedian * 100m
            : afterMedian > 0 ? decimal.MaxValue : 0m;
        var passed = growthPercentage <= MaximumGrowthPercentage;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{scenario}: {beforeMedian:F3} ms -> {afterMedian:F3} ms ({growthPercentage:F1}% growth, limit {MaximumGrowthPercentage:F1}%) {(passed ? "PASSED" : "FAILED")}"));
        return passed ? 0 : 1;
    }

    decimal? ReadScenarioMedian(string path, string scenario, string label)
    {
        if (!File.Exists(path))
        {
            _output.WriteLine($"Error: {label} file not found: {path}");
            return null;
        }

        var block = ExtractScenarioBlock(File.ReadAllText(path), scenario);
        var match = block is null ? null : Regex.Match(block, @"Median:\s*([0-9.]+) ms");
        if (match is not { Success: true } || !decimal.TryParse(match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var median))
        {
            _output.WriteLine($"Error: {label} file has no median for scenario '{scenario}'.");
            return null;
        }

        return median;
    }

    // The report's first block of that name; a cache directory with several drives repeats the blocks per drive.
    static string? ExtractScenarioBlock(string reportText, string scenarioName)
    {
        var match = Regex.Match(reportText,
            $@"(?:^|\r?\n)--- {Regex.Escape(scenarioName)} ---\r?\n([\s\S]*?)(?=\r?\n--- |\z)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
