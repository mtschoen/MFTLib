using System.Text;

namespace Benchmark;

public partial class BenchmarkRunner
{
    // Callers supply a nonempty sample set; midpoint preserves each value type's arithmetic.
    static T Median<T>(IEnumerable<T> samples, Func<T, T, T> midpoint)
    {
        var ordered = samples.Order().ToList();
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? midpoint(ordered[middle - 1], ordered[middle])
            : ordered[middle];
    }

    // The caller owns output; report lines are buffered for the optional file.
    sealed class ReportWriter(TextWriter output)
    {
        readonly StringBuilder _report = new();

        internal void Log(string line)
        {
            output.WriteLine(line);
            _report.AppendLine(line);
        }

        internal async Task SaveAsync(string? outputPath)
        {
            if (outputPath is null)
            {
                return;
            }

            var resolvedOutputPath = Path.GetFullPath(outputPath);
            await File.WriteAllTextAsync(resolvedOutputPath, _report.ToString()).ConfigureAwait(false);
            await output.WriteLineAsync($"Report saved to {resolvedOutputPath}").ConfigureAwait(false);
        }
    }
}
