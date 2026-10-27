using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    const int EnumerationTreeFanout = 8;

    async Task<int> RunEnumerationWalkAsync(string[] arguments)
    {
        if (!TryParseEnumerationWalkArguments(arguments, out var directories, out var filesPerDirectory,
                out var iterations, out var outputPath))
        {
            await _output.WriteLineAsync("Usage: Benchmark.exe enumeration-walk [--directories N] [--files-per-directory N] [--iterations K] [--out report.txt]").ConfigureAwait(false);
            return 1;
        }

        var report = new ReportWriter(_output);

        try
        {
            LogSystemInfo(report.Log);
            await MeasureEnumerationTreeAsync(directories, filesPerDirectory, iterations, report.Log).ConfigureAwait(false);
            await report.SaveAsync(outputPath).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException
                                              or InvalidOperationException or UnauthorizedAccessException)
        {
            await _output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    static bool TryParseEnumerationWalkArguments(string[] arguments, out int directories,
        out int filesPerDirectory, out int iterations, out string? outputPath)
    {
        directories = 10_000;
        filesPerDirectory = 1;
        iterations = 5;
        outputPath = null;
        if (arguments.Length % 2 != 0)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!seen.Add(arguments[index]))
            {
                return false;
            }
            var value = arguments[index + 1];
            if (arguments[index].Equals("--out", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }
                outputPath = value;
                continue;
            }
            if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }
            switch (arguments[index].ToLowerInvariant())
            {
                case "--directories": directories = number; break;
                case "--files-per-directory": filesPerDirectory = number; break;
                case "--iterations": iterations = number; break;
                default: return false;
            }
        }

        // Bound generated rows and their mapped columns before creating any files.
        return directories > 0 && filesPerDirectory >= 0 && iterations > 0 &&
               directories * (filesPerDirectory + 1L) <= 10_000_000;
    }

    async Task MeasureEnumerationTreeAsync(int directories, int filesPerDirectory, int iterations, Action<string> log)
    {
        var temporary = Directory.CreateTempSubdirectory("mftlib-enumeration-benchmark-");
        try
        {
            var root = Path.Combine(temporary.FullName, "tree");
            log($"Directories: {directories} (including root); fanout: {EnumerationTreeFanout}");
            log($"Files: {(long)directories * filesPerDirectory}; files per directory: {filesPerDirectory} (empty files)");
            log($"Walk: median of {iterations} runs per mode; alternating sequential/parallel order");
            log("Cache: warm filesystem cache; one untimed warm-up per mode; no OS cache eviction");
            log("Timing: producer traversal and block row writes only; excludes tree generation, block creation and disposal");
            log($"Permits: sequential 1 (base only); parallel shared budget up to {Environment.ProcessorCount}");
            GenerateEnumerationTree(root, directories, filesPerDirectory);
            var rows = checked((uint)(directories * (filesPerDirectory + 1)));
            var blockPath = Path.Combine(temporary.FullName, "walk.mlix");
            await MeasureEnumerationWalkAsync(root, blockPath, rows, sequential: true).ConfigureAwait(false);
            await MeasureEnumerationWalkAsync(root, blockPath, rows, sequential: false).ConfigureAwait(false);
            var sequential = new double[iterations];
            var parallel = new double[iterations];
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var sequentialFirst = iteration % 2 == 0;
                var first = await MeasureEnumerationWalkAsync(root, blockPath, rows, sequentialFirst).ConfigureAwait(false);
                var second = await MeasureEnumerationWalkAsync(root, blockPath, rows, !sequentialFirst).ConfigureAwait(false);
                sequential[iteration] = sequentialFirst ? first : second;
                parallel[iteration] = sequentialFirst ? second : first;
            }

            var sequentialMedian = PrintEnumerationMedian("sequential", sequential, rows, log);
            var parallelMedian = PrintEnumerationMedian("parallel", parallel, rows, log);
            log(parallelMedian > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Speedup (sequential / parallel): {sequentialMedian / parallelMedian:F3}x")
                : "Speedup (sequential / parallel): unavailable (parallel median is zero)");
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    static void GenerateEnumerationTree(string root, int directoryCount, int filesPerDirectory)
    {
        var paths = new string[directoryCount];
        paths[0] = root;
        for (var directory = 0; directory < directoryCount; directory++)
        {
            if (directory > 0)
            {
                paths[directory] = Path.Combine(paths[(directory - 1) / EnumerationTreeFanout],
                    string.Create(CultureInfo.InvariantCulture, $"d-{directory:D8}"));
            }
            Directory.CreateDirectory(paths[directory]);
            for (var file = 0; file < filesPerDirectory; file++)
            {
                File.WriteAllBytes(Path.Combine(paths[directory],
                    string.Create(CultureInfo.InvariantCulture, $"f-{file:D8}")), []);
            }
        }
    }

    async Task<double> MeasureEnumerationWalkAsync(string root, string blockPath, uint rows, bool sequential)
    {
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = blockPath,
            VolumeSerial = 0,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(rows),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(checked((rows * 20) + ((uint)root.Length * 2))),
            ScanShape = ScanShape.ForEnumeration(null, new DirectoryWalkOptions(), root)
        });
        var writer = new BlockWriter(block);
        var producer = new EnumerationProducer(new EnumerationProducerOptions { RootDirectory = root, Drive = 'T' });
        using var lease = await EnumerationWalkLimit.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        // The existing per-walk ceiling prevents taking any extra shared permits in sequential mode.
        var started = _timeProvider.GetTimestamp();
        producer.Produce(writer, null, lease, CancellationToken.None,
            maximumWorkers: sequential ? 1 : int.MaxValue);
        var elapsed = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
        var header = block.Header;
        if (header.IsCapacityExhausted || header.AccessDeniedSubtreeCount != 0 || header.RowCount != rows)
        {
            throw new InvalidOperationException($"Incomplete enumeration: expected {rows} rows, produced {header.RowCount}, skipped {header.AccessDeniedSubtreeCount}, capacity exhausted {header.IsCapacityExhausted}.");
        }
        return elapsed;
    }

    static double PrintEnumerationMedian(string mode, double[] samples, uint rows, Action<string> log)
    {
        var median = Median(samples, static (left, right) => (left + right) / 2);
        log($"--- enumeration-walk-{mode} ---");
        log(string.Create(CultureInfo.InvariantCulture, $"  Median: {median:F3} ms; Rows: {rows}"));
        return median;
    }
}
