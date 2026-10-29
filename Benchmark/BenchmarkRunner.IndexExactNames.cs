using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    static readonly string[] MultiNameQueryNames = [IndexQueryName, ".git", "HEAD", "objects", "refs"];

    /// <summary>
    ///     Times one <see cref="FileIndexSet.SearchExactNames" /> call for the discovery-shaped name list and reports
    ///     how many rows differ from the per-name exact <see cref="FileIndexSet.Search" /> results (0 when equivalent).
    /// </summary>
    internal void PrintMultiNameExactMedian(FileIndex index, int iterations, Action<string> log)
    {
        var set = new FileIndexSet([index]);
        var results = set.SearchExactNames(MultiNameQueryNames);
        var differing = CountMultiNameDifferingRows(set, results);
        var samples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var started = _timeProvider.GetTimestamp();
            results = set.SearchExactNames(MultiNameQueryNames);
            samples[iteration] = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
        }

        var median = Median(samples, static (left, right) => (left + right) / 2);
        var matches = results.Sum(list => list.Count);
        log(string.Create(CultureInfo.InvariantCulture,
            $"  Multi-name exact ({MultiNameQueryNames.Length} names): median {median:F3} ms; Matches: {matches}; Differing rows vs per-name search: {differing}"));
    }

    static int CountMultiNameDifferingRows(FileIndexSet set, IReadOnlyList<IReadOnlyList<FileEntry>> results)
    {
        var differing = 0;
        foreach (var (name, found) in MultiNameQueryNames.Zip(results))
        {
            var single = set.Search(new SearchQuery(name, NameMatchMode.Exact));
            var expected = new HashSet<FileEntry>(single);
            var actual = new HashSet<FileEntry>(found);
            differing += expected.Count(entry => !actual.Contains(entry)) + actual.Count(entry => !expected.Contains(entry));
            differing += Math.Abs(single.Count - found.Count);
        }

        return differing;
    }
}
