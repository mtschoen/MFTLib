using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

        if (outputIndex >= 0 && outputPath is null ||
            !TryParseIndexArguments(arguments, out var rows, out var cacheDirectory, out var iterations))
        {
            _writeLineToConsole(
                "Usage: Benchmark.exe index <--synthetic N|--cache-directory path> [--iterations K] [--out report.txt]");
            return 1;
        }

        // Everything the run prints, so --out saves exactly what the console showed.
        var report = new StringBuilder();
        void Log(string line)
        {
            _writeLineToConsole(line);
            report.AppendLine(line);
        }

        try
        {
            LogSystemInfo(Log);
            var exitCode = cacheDirectory is null
                ? await RunSyntheticIndexAsync(rows, iterations, Log)
                : await RunCachedIndexAsync(cacheDirectory, iterations, Log);
            if (exitCode == 0 && outputPath is not null)
            {
                var resolvedOutputPath = Path.GetFullPath(outputPath);
                _writeAllText(resolvedOutputPath, report.ToString());
                _writeLineToConsole($"Report saved to {resolvedOutputPath}");
            }

            return exitCode;
        }
        catch (Exception exception)
        {
            _writeLineToConsole($"Error: {exception.Message}");
            return 1;
        }
    }

    void LogSystemInfo(Action<string> log)
    {
        log("System Info");
        log($"  Build:   {_systemInfo._getBuildConfiguration()}");
        log($"  OS:      {_systemInfo._getWmiValue("Win32_OperatingSystem", "Caption")} ({Environment.OSVersion.Version})");
        log($"  CPU:     {_systemInfo._getWmiValue("Win32_Processor", "Name")}");
        log($"  Threads: {Environment.ProcessorCount}");
        log($"  RAM:     {_systemInfo._getInstalledMemoryGb()} GB");
        log($"  Disk:    {_systemInfo._getDiskModel(AppContext.BaseDirectory)}");
        log($"  .NET:    {RuntimeInformation.FrameworkDescription}");
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
                VolumeSerial = 0,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = BlockLayout.ComputeSlotCapacity(rows),
                NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(checked(rows * 30 + (uint)directory.Length * 2))
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
        foreach (var candidate in cached.OrderBy(candidate => candidate.File.Drive.Letter))
        {
            if (candidate.Availability != CachedBlockAvailability.Available || candidate.RootDirectory is null ||
                candidate.CacheTag is not { } cacheTag || candidate.ProducerKind is not { } producer)
            {
                log($"Error: {candidate.File.Path}: {candidate.Availability}{(candidate.Validation is { } reason ? $" ({reason})" : "")}.");
                exitCode = 1;
                continue;
            }

            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                CacheDirectory = directory,
                CacheMode = CacheMode.ExistingOnly,
                CacheTag = cacheTag,
                Drives = [new IndexedDrive(candidate.File.Drive, candidate.RootDirectory, candidate.File.VolumeSerial,
                    producer)]
            }, CancellationToken.None);
            using var borrow = index.BorrowCurrentSnapshot();
            var drive = borrow.Snapshot.FindDriveBlock(candidate.File.Drive);
            if (drive is null)
            {
                log($"Error: Drive {candidate.File.Drive}: {index.Drives[0].State}.");
                exitCode = 1;
                continue;
            }

            PrintIndexMetrics(index, drive, iterations, log);
        }
        return exitCode;
    }

    void PrintIndexMetrics(FileIndex index, DriveBlock drive, int iterations, Action<string> log)
    {
        var header = drive.Block.Header;
        var bytes = drive.Block.Length;
        log($"Drive: {drive.Drive}");
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

    void PrintIndexMedian(string label, Func<IReadOnlyList<FileEntry>> query, int iterations, Action<string> log)
    {
        var samples = new double[iterations];
        var matches = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            matches = query().Count;
            stopwatch.Stop();
            samples[iteration] = _getStopwatchElapsedMs(stopwatch);
        }
        Array.Sort(samples);
        var middle = iterations / 2;
        var median = iterations % 2 == 0 ? (samples[middle - 1] + samples[middle]) / 2 : samples[middle];
        log($"--- {label} ---");
        log(string.Create(CultureInfo.InvariantCulture, $"  Median: {median:F3} ms; Matches: {matches}"));
    }
}
