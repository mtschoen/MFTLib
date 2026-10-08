using System.Diagnostics;
using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

/// <summary>
///     Spike entry point. Usage:
///     Benchmark.exe names generate ROWS DIRECTORY
///     Benchmark.exe names measure DIRECTORY [RUNS]
///     The measured variants use only public API.
/// </summary>
static class NameAccessorSpike
{
    struct LargestVisitor(PriorityQueue<FileEntry, long> best, int count) : IIndexRowVisitor
    {
        public void Visit(in IndexRow row)
        {
            if (row.IsDeleted || !row.IsSizeKnown) return;
            best.Enqueue(row.ToEntry(), row.Size);
            if (best.Count > count) best.Dequeue();
        }
    }

    sealed class LargestClassVisitor(PriorityQueue<FileEntry, long> best, int count) : IIndexRowVisitor
    {
        public void Visit(in IndexRow row)
        {
            if (row.IsDeleted || !row.IsSizeKnown) return;
            best.Enqueue(row.ToEntry(), row.Size);
            if (best.Count > count) best.Dequeue();
        }
    }

    static List<long> Drain(PriorityQueue<FileEntry, long> best)
    {
        var sizes = new List<long>();
        while (best.TryDequeue(out var entry, out _)) sizes.Add(entry.Size);
        sizes.Sort();
        return sizes;
    }

    static List<long> LargestEnumerate(FileIndex index, int count)
    {
        var best = new PriorityQueue<FileEntry, long>(count + 1);
        foreach (var entry in index.Enumerate(new SearchQuery(null, Directories: false)))
        {
            if (entry.IsDeleted || !entry.IsSizeKnown) continue;
            best.Enqueue(entry, entry.Size);
            if (best.Count > count) best.Dequeue();
        }

        return Drain(best);
    }

    static List<long> LargestVisitorGeneric(FileIndex index, int count)
    {
        var best = new PriorityQueue<FileEntry, long>(count + 1);
        var visitor = new LargestVisitor(best, count);
        index.ForEachRow(new SearchQuery(null, Directories: false), ref visitor);
        return Drain(best);
    }

    static List<long> LargestVisitorInterface(FileIndex index, int count)
    {
        var best = new PriorityQueue<FileEntry, long>(count + 1);
        index.ForEachRow(new SearchQuery(null, Directories: false), new LargestClassVisitor(best, count));
        return Drain(best);
    }

    static Dictionary<string, int> NaiveDuplicates(FileIndex index)
    {
        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Enumerate(new SearchQuery(null, Directories: false)))
        {
            var name = entry.Name;
            if (name.Length == 0) continue;
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add(entry);
        }

        return Summarize(byName);
    }

    static Dictionary<string, int> Summarize(Dictionary<string, List<FileEntry>> byName)
    {
        var summary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, list) in byName) if (list.Count > 1) summary[name] = list.Count;
        return summary;
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is ["generate", var rowText, var generateDirectory])
        {
            var stopwatch = Stopwatch.StartNew();
            NameAccessorSpikeGenerator.Generate(generateDirectory, uint.Parse(rowText, CultureInfo.InvariantCulture));
            Console.WriteLine($"generated {rowText} rows in {stopwatch.Elapsed.TotalSeconds:F1} s");
            return 0;
        }

        if (arguments.Length >= 2 && arguments[0] == "measure")
        {
            return await MeasureAsync(arguments[1], arguments.Length > 2 ? int.Parse(arguments[2], CultureInfo.InvariantCulture) : 5);
        }

        Console.WriteLine("Usage: names generate ROWS DIRECTORY | names measure DIRECTORY [RUNS]");
        return 1;
    }

    static async Task<int> MeasureAsync(string directory, int runs)
    {
        var candidate = CacheDirectory.InspectCached(directory).Single();
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheDirectory = directory,
            InitialOpenCacheOnly = true,
            CacheTag = candidate.CacheTag!.Value,
            Drives = [new IndexedDrive(candidate.File.DriveLetter, candidate.RootDirectory!, candidate.File.VolumeSerial)]
        }, CancellationToken.None);
        long rowCount = index.Drives.Sum(drive => (long)drive.Block.LiveRowCount);
        Console.WriteLine($"rows (LiveRowCount): {rowCount}; runs: {runs}");

        var duplicateVariants = new (string Name, Func<Dictionary<string, int>> Run)[]
        {
            ("LIB", () => index.DuplicateNames().ToDictionary(group => group.Name, group => group.Entries.Count, StringComparer.OrdinalIgnoreCase)),
            ("A copyname", () => Summarize(ConsumerSieve.FindWithCopyName(index, rowCount, default))),
            ("B iface", () => Summarize(ConsumerSieve.FindWithVisitor(index, rowCount, generic: false, default))),
            ("B generic", () => Summarize(ConsumerSieve.FindWithVisitor(index, rowCount, generic: true, default))),
            ("NAIVE", () => NaiveDuplicates(index)),
        };
        var largestVariants = new (string Name, Func<List<long>> Run)[]
        {
            ("LIB", () => index.Largest(25).Select(entry => entry.Size).OrderBy(size => size).ToList()),
            ("ENUMERATE", () => LargestEnumerate(index, 25)),
            ("B iface", () => LargestVisitorInterface(index, 25)),
            ("B generic", () => LargestVisitorGeneric(index, 25)),
        };

        Console.WriteLine("== Duplicate names ==");
        Report(duplicateVariants, runs, CompareDuplicates);
        Console.WriteLine("== Largest(25) ==");
        Report(largestVariants, runs, CompareSizes);
        return 0;
    }

    static string CompareDuplicates(Dictionary<string, int> reference, Dictionary<string, int> actual)
    {
        var missing = reference.Count(pair => !actual.TryGetValue(pair.Key, out var count) || count != pair.Value);
        var extra = actual.Count(pair => !reference.ContainsKey(pair.Key));
        return missing == 0 && extra == 0 ? $"OK ({actual.Count} groups)" : $"MISMATCH missing/different={missing} extra={extra}";
    }

    static string CompareSizes(List<long> reference, List<long> actual) =>
        reference.SequenceEqual(actual) ? $"OK ({actual.Count} sizes)" : "MISMATCH";

    static void Report<TResult>((string Name, Func<TResult> Run)[] variants, int runs, Func<TResult, TResult, string> compare)
    {
        TResult? reference = default;
        double referenceMedian = 0;
        foreach (var (name, run) in variants)
        {
            var result = run(); // warm-up
            if (reference is null) reference = result;
            var correctness = compare(reference, result);
            var times = new List<double>();
            var allocations = new List<long>();
            var peaks = new List<long>();
            for (var iteration = 0; iteration < runs; iteration++)
            {
                result = default!;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                var baseline = GC.GetTotalMemory(false);
                long peak = baseline;
                using var stop = new CancellationTokenSource();
                var sampler = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        peak = Math.Max(peak, GC.GetTotalMemory(false));
                        Thread.Sleep(2);
                    }
                });
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var stopwatch = Stopwatch.StartNew();
                result = run();
                stopwatch.Stop();
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                stop.Cancel();
                sampler.Wait();
                peaks.Add(peak - baseline);
                times.Add(stopwatch.Elapsed.TotalMilliseconds);
                GC.KeepAlive(result);
            }

            times.Sort();
            allocations.Sort();
            peaks.Sort();
            var median = times[times.Count / 2];
            if (referenceMedian == 0) referenceMedian = median;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-11} median {median,9:F1} ms  min-max {times[0]:F1}-{times[^1]:F1}  alloc(median) {allocations[allocations.Count / 2],14:N0}  peakManagedDelta(median) {peaks[peaks.Count / 2],14:N0}  ratio {median / referenceMedian:F2}x  check: {correctness}"));
        }
    }
}
