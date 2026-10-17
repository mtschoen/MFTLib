
namespace Benchmark;

public partial class BenchmarkRunner
{
    readonly TextWriter _output;
    readonly TimeProvider _timeProvider;

    public BenchmarkRunner(TextWriter output, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = TextWriter.Synchronized(output);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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

        _output.WriteLine("Usage: Benchmark.exe <index|compare> ...");
        return Task.FromResult(1);
    }
}
