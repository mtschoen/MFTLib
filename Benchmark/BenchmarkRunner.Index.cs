using System.Diagnostics;
using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    const string IndexQueryName = "file-0000000001";

    async Task<int> RunIndexAsync(string[] arguments)
    {
        if (!TryParseIndexArguments(arguments, out var rows, out var cacheDirectory, out var iterations))
        {
            _writeLineToConsole("Usage: Benchmark.exe index <--synthetic N|--cache-directory path> [--iterations K]");
            return 1;
        }

        try
        {
            return cacheDirectory is null
                ? await RunSyntheticIndexAsync(rows, iterations)
                : await RunCachedIndexAsync(cacheDirectory, iterations);
        }
        catch (Exception exception)
        {
            _writeLineToConsole($"Error: {exception.Message}");
            return 1;
        }
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

    async Task<int> RunSyntheticIndexAsync(uint rows, int iterations)
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
            return await RunCachedIndexAsync(directory, iterations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    async Task<int> RunCachedIndexAsync(string directory, int iterations)
    {
        var cached = CacheDirectory.InspectCached(directory);
        if (cached.Count == 0)
        {
            _writeLineToConsole("Error: No cache blocks found.");
            return 1;
        }

        var exitCode = 0;
        foreach (var candidate in cached.OrderBy(candidate => candidate.File.DriveLetter))
        {
            if (candidate.Availability != CachedBlockAvailability.Available || candidate.RootDirectory is null ||
                candidate.CacheTag is not { } cacheTag)
            {
                _writeLineToConsole($"Error: {candidate.File.Path}: {candidate.Availability} ({candidate.Validation}).");
                exitCode = 1;
                continue;
            }

            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                CacheDirectory = directory,
                InitialOpenCacheOnly = true,
                CacheTag = cacheTag,
                Drives = [new IndexedDrive(candidate.File.DriveLetter, candidate.RootDirectory, candidate.File.VolumeSerial)]
            }, CancellationToken.None);
            using var borrow = index.BorrowCurrentSnapshot();
            var drive = borrow.Snapshot.FindDriveBlock(candidate.File.DriveLetter);
            if (drive is null)
            {
                _writeLineToConsole($"Error: Drive {candidate.File.DriveLetter}: {index.Drives[0].State}.");
                exitCode = 1;
                continue;
            }

            PrintIndexMetrics(index, drive, iterations);
        }
        return exitCode;
    }

    void PrintIndexMetrics(FileIndex index, DriveBlock drive, int iterations)
    {
        var header = drive.Block.Header;
        var bytes = drive.Block.Length;
        _writeLineToConsole($"Drive: {drive.DriveLetter}");
        _writeLineToConsole($"  Rows: {header.RowCount}");
        _writeLineToConsole($"  Slot capacity: {header.SlotCapacity}");
        _writeLineToConsole($"  File bytes: {bytes}");
        _writeLineToConsole(string.Create(CultureInfo.InvariantCulture,
            $"  Name-pool bytes: {header.NamePoolUsed} ({100.0 * header.NamePoolUsed / bytes:F2}% of file)"));
        _writeLineToConsole($"  Name-pool capacity bytes: {header.NamePoolCapacity}");
        _writeLineToConsole($"  Query: {IndexQueryName} (exact and substring searches; median of {iterations} runs)");
        // Touch the mapped pages and initialize both query paths before collecting warm samples.
        var exactQuery = new SearchQuery(IndexQueryName, NameMatchMode.Exact);
        var substringQuery = new SearchQuery(IndexQueryName);
        index.Search(exactQuery);
        index.Search(substringQuery);
        PrintIndexMedian("Exact", () => index.Search(exactQuery), iterations);
        PrintIndexMedian("Substring", () => index.Search(substringQuery), iterations);
    }

    void PrintIndexMedian(string label, Func<IReadOnlyList<FileEntry>> query, int iterations)
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
        _writeLineToConsole(string.Create(CultureInfo.InvariantCulture, $"  {label} median: {median:F3} ms; Matches: {matches}"));
    }
}
