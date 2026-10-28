namespace Benchmark;

public partial class BenchmarkRunner
{
    readonly TextWriter _output;
    readonly TimeProvider _timeProvider;
    readonly Func<ScenarioMemoryRequest, Task<ScenarioMemoryResult>> _measureMemory;

    public BenchmarkRunner(TextWriter output, TimeProvider? timeProvider = null)
        : this(output, timeProvider, request => ScenarioMemoryProcess.MeasureAsync(request))
    {
    }

    internal BenchmarkRunner(TextWriter output, TimeProvider? timeProvider,
        Func<ScenarioMemoryRequest, Task<ScenarioMemoryResult>> measureMemory)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(measureMemory);
        _output = TextWriter.Synchronized(output);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _measureMemory = measureMemory;
    }

    public Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0].Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            return RunIndexAsync(arguments[1..]);
        }

        if (arguments.Length > 0 && arguments[0].Equals("index-storage", StringComparison.OrdinalIgnoreCase))
        {
            return RunIndexStorageAsync(arguments[1..]);
        }

        if (arguments.Length > 0 && arguments[0].Equals("compare", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(RunCompare(arguments[1..]));
        }

        if (arguments.Length > 0 && arguments[0].Equals("native-timings", StringComparison.OrdinalIgnoreCase))
        {
            return RunNativeTimingsAsync(arguments[1..]);
        }

        if (arguments.Length > 0 && arguments[0].Equals("enumeration-walk", StringComparison.OrdinalIgnoreCase))
        {
            return RunEnumerationWalkAsync(arguments[1..]);
        }

        _output.WriteLine("Usage: Benchmark.exe <index|index-storage|compare|native-timings|enumeration-walk> ...");
        return Task.FromResult(1);
    }
}
