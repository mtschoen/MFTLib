using System.Globalization;
using System.Runtime.InteropServices;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    const string IndexQueryName = "file-0000000001";

    async Task<int> RunIndexAsync(string[] arguments)
    {
        var outputIndex = Array.FindIndex(arguments, argument => argument.Equals("--out", StringComparison.OrdinalIgnoreCase));
        string? outputPath = null;
        if (outputIndex >= 0 && outputIndex + 1 < arguments.Length)
        {
            outputPath = arguments[outputIndex + 1];
            arguments = [.. arguments[..outputIndex], .. arguments[(outputIndex + 2)..]];
        }

        if ((outputIndex >= 0 && outputPath is null) ||
            !TryParseIndexArguments(arguments, out var rows, out var cacheDirectory, out var iterations))
        {
            await _output.WriteLineAsync(
                "Usage: Benchmark.exe index <--synthetic N|--cache-directory path> [--iterations K] [--out report.txt]").ConfigureAwait(false);
            return 1;
        }

        var report = new ReportWriter(_output);

        try
        {
            LogSystemInfo(report.Log);
            var exitCode = cacheDirectory is null
                ? await RunSyntheticIndexAsync(rows, iterations, report.Log)
                : await RunCachedIndexAsync(cacheDirectory, iterations, report.Log);
            if (exitCode == 0)
            {
                await report.SaveAsync(outputPath).ConfigureAwait(false);
            }

            return exitCode;
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    static void LogSystemInfo(Action<string> log)
    {
        var hardware = OperatingSystem.IsWindows()
            ? SystemInfo.QueryWindowsHardware(AppContext.BaseDirectory)
            : SystemInfo.DescribeWithoutWmi();
        log("System Info");
        log($"  Commit: {GetBuildCommit()}");
        log($"  Build:   {SystemInfo.DefaultGetBuildConfiguration()}");
        log($"  OS:      {hardware.OperatingSystem} ({Environment.OSVersion.Version})");
        log($"  CPU:     {hardware.Processor}");
        log($"  Threads: {Environment.ProcessorCount}");
        if (hardware.MemoryWarning is not null)
        {
            log(hardware.MemoryWarning);
        }
        log($"  RAM:     {hardware.Memory}");
        log($"  Disk:    {hardware.DiskModel}");
        log($"  .NET:    {RuntimeInformation.FrameworkDescription}");
    }

    internal static string GetBuildCommit() =>
        ParseBuildCommit(typeof(BenchmarkRunner).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single().InformationalVersion);

    internal static string ParseBuildCommit(string informational)
    {
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        if (separator < 0 || separator == informational.Length - 1)
        {
            throw new InvalidOperationException(
                "Benchmark build has no embedded source revision; rebuild from a Git checkout.");
        }

        return informational[(separator + 1)..];
    }

    static bool TryParseIndexArguments(string[] arguments, out uint rows, out string? cacheDirectory,
        out int iterations)
    {
        rows = 0;
        cacheDirectory = null;
        iterations = 3;
        if (arguments.Length != 2 && arguments.Length != 4)
        {
            return false;
        }
        if (arguments.Length == 4 &&
            (!arguments[2].Equals("--iterations", StringComparison.OrdinalIgnoreCase) ||
             !int.TryParse(arguments[3], CultureInfo.InvariantCulture, out iterations) || iterations <= 0))
        {
            return false;
        }
        if (arguments[0].Equals("--cache-directory", StringComparison.OrdinalIgnoreCase))
        {
            cacheDirectory = arguments[1];
            return !string.IsNullOrWhiteSpace(cacheDirectory);
        }

        // Both mapped spans and the UTF-16 pool must fit their existing format limits.
        return arguments[0].Equals("--synthetic", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(arguments[1], CultureInfo.InvariantCulture, out rows) && rows > 0 &&
               rows <= 100_000_000;
    }

    async Task<int> RunSyntheticIndexAsync(uint rows, int iterations, Action<string> log)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mftlib-index-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using (var block = BlockFile.Create(new BlockFileCreateOptions
            {
                Path = Path.Combine(directory, "T-00000000.mlix"),
                Root = new VolumeKey('T'),
                VolumeSerial = 0,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = BlockLayout.ComputeSlotCapacity(rows),
                NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(checked((rows * 30) + ((uint)directory.Length * 2))),
                ScanShape = ScanShape.ForEnumeration(new DirectoryWalkOptions(), directory)
            }))
            {
                var writer = new BlockWriter(block);
                writer.TryWriteRow(0, directory, new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                for (uint row = 1; row < rows; row++)
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"file-{row:D10}");
                    writer.TryWriteRow(row, name, new RowColumns(0, RowFlags.InUse, 0, row, 0, 0));
                }
                writer.Complete(DateTime.UtcNow, null);
            }
            return await RunCachedIndexAsync(directory, iterations, log);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    async Task<int> RunCachedIndexAsync(string directory, int iterations, Action<string> log)
    {
        var cached = CacheDirectory.InspectCached(directory);
        if (cached.Count == 0)
        {
            log("Error: No cache blocks found.");
            return 1;
        }

        var exitCode = 0;
        foreach (var candidate in cached.OrderBy(candidate => candidate.File.Root))
        {
            if (candidate.Availability != CachedBlockAvailability.Available || candidate.RootDirectory is null ||
                candidate.CacheTag is not { } cacheTag || candidate.ProducerKind is not { } producer ||
                candidate.ScanShape is not { } storedShape)
            {
                log($"Error: {candidate.File.Path}: {candidate.Availability}{(candidate.Validation is { } reason ? $" ({reason})" : "")}.");
                exitCode = 1;
                continue;
            }

            // The benchmark queries with default row-shaping options, so it names a block shaped by
            // any others here. A block replaced after inspection is refused by the cache-only open,
            // which never deletes a block, and reported as a failed drive below.
            var benchmarkShape = producer == ProducerKind.Enumeration
                ? ScanShape.ForEnumeration(new DirectoryWalkOptions(), candidate.RootDirectory)
                : ScanShape.ForMft(includeFreed: false);
            if (storedShape != benchmarkShape)
            {
                log($"Error: {candidate.File.Path}: unsupported scan shape (built with other row-shaping options); left intact.");
                exitCode = 1;
                continue;
            }

            var lines = new List<string>();
            await using (var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                CacheDirectory = directory,
                CacheMode = CacheMode.ExistingOnly,
                CacheTag = cacheTag,
                Root = new IndexedRoot(candidate.File.Root, candidate.RootDirectory, candidate.File.VolumeSerial,
                    producer)
            }, CancellationToken.None))
            {
                using var borrow = index.BorrowCurrentSnapshot();
                var root = borrow.RootBlock;
                if (root is null)
                {
                    log($"Error: Root {candidate.File.Root}: {index.Status.State}.");
                    exitCode = 1;
                    continue;
                }

                PrintIndexMetrics(index, root, iterations, lines.Add);
            }

            var input = new IndexMemoryInput(candidate.File.Root.ToString(), candidate.RootDirectory,
                candidate.File.VolumeSerial, producer, cacheTag.PackedFourCc, cacheTag.Version, storedShape);
            await AppendIndexMemoryAsync(lines, directory, iterations, input, log).ConfigureAwait(false);
        }
        return exitCode;
    }

    async Task AppendIndexMemoryAsync(List<string> lines, string directory, int iterations,
        IndexMemoryInput input, Action<string> log)
    {
        var exact = await _measureMemory(
            new ScenarioMemoryRequest("exact-search", directory, iterations, input)).ConfigureAwait(false);
        var substring = await _measureMemory(
            new ScenarioMemoryRequest("substring-search", directory, iterations, input)).ConfigureAwait(false);
        ScenarioMemoryResult? pending = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    PrintMemory(pending, log);
                }

                pending = line == "--- exact-search ---" ? exact : substring;
            }

            log(line);
        }

        if (pending is not null)
        {
            PrintMemory(pending, log);
        }
    }

    void PrintIndexMetrics(FileIndex index, RootBlock root, int iterations, Action<string> log)
    {
        var header = root.Block.Header;
        var bytes = root.Block.Length;
        log($"Root: {root.Root}");
        log($"  Rows: {header.RowCount}");
        log($"  Slot capacity: {header.SlotCapacity}");
        log($"  File bytes: {bytes}");
        log(string.Create(CultureInfo.InvariantCulture,
            $"  Name-pool bytes: {header.NamePoolUsed} ({100.0 * header.NamePoolUsed / bytes:F2}% of file)"));
        log($"  Name-pool capacity bytes: {header.NamePoolCapacity}");
        log($"  Query: {IndexQueryName} (median of {iterations} runs)");
        // Touch the mapped pages and initialize both query paths before collecting warm samples.
        var exactQuery = new SearchQuery(IndexQueryName, NameMatchMode.Exact);
        var substringQuery = new SearchQuery(IndexQueryName);
        index.Search(exactQuery);
        index.Search(substringQuery);
        PrintIndexMedian("exact-search", () => index.Search(exactQuery), iterations, log);
        PrintIndexMedian("substring-search", () => index.Search(substringQuery), iterations, log);
    }

    internal void PrintIndexMedian(string label, Func<IReadOnlyList<FileEntry>> query, int iterations, Action<string> log)
    {
        var samples = new double[iterations];
        var matches = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var started = _timeProvider.GetTimestamp();
            matches = query().Count;
            samples[iteration] = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
        }
        var median = Median(samples, static (left, right) => (left + right) / 2);
        log($"--- {label} ---");
        log(string.Create(CultureInfo.InvariantCulture, $"  Median: {median:F3} ms; Matches: {matches}"));
    }
}
