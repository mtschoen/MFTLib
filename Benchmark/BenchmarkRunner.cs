using System.Diagnostics;

namespace Benchmark;

public partial class BenchmarkRunner
{
    // File system seams
    internal Func<string, bool> _fileExists = File.Exists;
    internal Func<string, string> _readAllText = File.ReadAllText;
    internal Action<string, string> _writeAllText = File.WriteAllText;

    // Measurement and console seams
    internal Func<Stopwatch, double> _getStopwatchElapsedMs = stopwatch => stopwatch.Elapsed.TotalMilliseconds;
    internal Action<string> _writeLineToConsole = Console.WriteLine;

    // System info seam
    internal SystemInfo _systemInfo = new();

    public Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0].Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            return RunIndexAsync(arguments[1..]);
        }

        if (arguments.Length > 0 && arguments[0].Equals("compare", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(RunCompare(arguments[1..]));
        }

        _writeLineToConsole("Usage: Benchmark.exe <index|compare> ...");
        return Task.FromResult(1);
    }
}
