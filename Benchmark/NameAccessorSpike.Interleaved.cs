using System.Diagnostics;
using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

static partial class NameAccessorSpike
{
    static int _lastScanPassCount;

    static async Task<int> MeasureInterleavedAsync(string directory, int runs)
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
        Console.WriteLine($"rows (LiveRowCount): {rowCount}; runs: {runs}; IndexRow shape: {IndexRowShape}");

        int libPassCount;
        using (var borrow = index.BorrowCurrentSnapshot())
        {
            _ = DuplicateNameFinder.Find(borrow.Snapshot, DuplicateNameSieveOptions.Default, out var statistics);
            libPassCount = statistics.CandidatesPerPass.Count + 1;
        }

        Func<Dictionary<string, int>> WithKnob(bool general, Func<Dictionary<string, int>> body) => () =>
        {
            FileIndex.UseGeneralRowLoopForSpike = general;
            return body();
        };

        var duplicateVariants = new (string Name, Func<Dictionary<string, int>> Run)[]
        {
            ("LIB", WithKnob(false, () =>
            {
                _lastScanPassCount = libPassCount;
                return index.DuplicateNames().ToDictionary(group => group.Name, group => group.Entries.Count, StringComparer.OrdinalIgnoreCase);
            })),
            ("LIB direct", WithKnob(false, () =>
            {
                using var borrow = index.BorrowCurrentSnapshot();
                var groups = DuplicateNameFinder.FindDirect(borrow.Snapshot, out _lastScanPassCount);
                return groups.ToDictionary(group => group.Name, group => group.Entries.Count, StringComparer.OrdinalIgnoreCase);
            })),
            ("B generic (old loop)", WithKnob(true, () =>
                Summarize(ConsumerSieve.FindWithVisitor(index, rowCount, generic: true, default)))),
            ("B generic (new loop)", WithKnob(false, () =>
                Summarize(ConsumerSieve.FindWithVisitor(index, rowCount, generic: true, default)))),
            ("B direct (old loop)", WithKnob(true, () =>
                Summarize(DirectSieve.Find(index, rowCount, out _lastScanPassCount, default)))),
            ("B direct (new loop)", WithKnob(false, () =>
                Summarize(DirectSieve.Find(index, rowCount, out _lastScanPassCount, default)))),
            ("C foreach", WithKnob(false, () =>
                Summarize(ForeachSieve.Find(index, rowCount, out _lastScanPassCount, default)))),
        };

        Func<List<long>> LargestWithKnob(bool general, Func<List<long>> body) => () =>
        {
            FileIndex.UseGeneralRowLoopForSpike = general;
            _lastScanPassCount = 1;
            return body();
        };

        var largestVariants = new (string Name, Func<List<long>> Run)[]
        {
            ("LIB", LargestWithKnob(false, () => index.Largest(25).Select(entry => entry.Size).OrderBy(size => size).ToList())),
            ("B generic (old loop)", LargestWithKnob(true, () => LargestVisitorGeneric(index, 25))),
            ("B direct (new loop)", LargestWithKnob(false, () => LargestVisitorGeneric(index, 25))),
            ("C foreach", LargestWithKnob(false, () => ForeachSieve.Largest(index, 25))),
        };

        Console.WriteLine("== Duplicate names ==");
        ReportInterleaved(duplicateVariants, runs, rowCount, CompareDuplicates);
        Console.WriteLine("== Largest(25) ==");
        ReportInterleaved(largestVariants, runs, rowCount, CompareSizes);
        FileIndex.UseGeneralRowLoopForSpike = false;
        return 0;
    }

    static string IndexRowShape =>
        typeof(IndexRow).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Any(member => member.Name == "_driveLetter") ? "original" : "lean";

    static void ReportInterleaved<TResult>((string Name, Func<TResult> Run)[] variants, int runs, long rowCount,
        Func<TResult, TResult, string> compare)
    {
        var times = variants.Select(_ => new List<double>()).ToArray();
        var passCounts = new int[variants.Length];
        var correctness = new string[variants.Length];
        TResult reference = default!;
        for (var variant = 0; variant < variants.Length; variant++)
        {
            var result = variants[variant].Run();
            passCounts[variant] = _lastScanPassCount;
            if (variant == 0) reference = result;
            correctness[variant] = compare(reference, result);
        }

        for (var round = 0; round < runs; round++)
        {
            for (var variant = 0; variant < variants.Length; variant++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                var stopwatch = Stopwatch.StartNew();
                var result = variants[variant].Run();
                stopwatch.Stop();
                GC.KeepAlive(result);
                times[variant].Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        var libNanoseconds = 0.0;
        var libDirectNanoseconds = 0.0;
        for (var variant = 0; variant < variants.Length; variant++)
        {
            times[variant].Sort();
            var median = times[variant][runs / 2];
            var nanoseconds = median * 1e6 / rowCount / passCounts[variant];
            if (variant == 0) libNanoseconds = nanoseconds;
            if (variants[variant].Name == "LIB direct") libDirectNanoseconds = nanoseconds;
            var directRatio = libDirectNanoseconds > 0 ? $"{nanoseconds / libDirectNanoseconds:F2}x" : "n/a";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{variants[variant].Name,-22} median {median,9:F1} ms  min {times[variant][0]:F1}  max {times[variant][^1]:F1}  passes {passCounts[variant]}  {nanoseconds:F3} ns/row/pass  vsLIB {nanoseconds / libNanoseconds:F2}x  vsLIBdirect {directRatio}  check: {correctness[variant]}"));
        }
    }
}
