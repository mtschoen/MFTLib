using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    async Task<int> RunIndexGrowthAsync(string[] arguments)
    {
        if (!TryParseGrowthArguments(arguments, out var rows, out var iterations))
        {
            await _output.WriteLineAsync(
                $"Usage: Benchmark.exe index-growth --synthetic N [--iterations K] (N from {GrowthSyntheticVolume.MinimumRows} to 20000000)")
                .ConfigureAwait(false);
            return 1;
        }

        var directory = Directory.CreateTempSubdirectory("mftlib-index-growth-");
        try
        {
            var volume = new GrowthSyntheticVolume(rows);
            LogSystemInfo(_output.WriteLine);
            LogGrowthVolume(volume);
            var basePath = Path.Combine(Directory.CreateDirectory(Path.Combine(directory.FullName, "base")).FullName,
                CacheDirectory.BlockFileName(GrowthSyntheticVolume.Volume, 0));
            volume.Write(basePath);
            var targetPath = Path.Combine(directory.FullName, "replacement.mlix");
            using (var source = BlockFile.Open(basePath, 0, out var validation) ??
                                throw new InvalidOperationException($"The synthetic growth block did not open: {validation}."))
            {
                LogPhases("growth-verbatim", MeasureGrowths(source, targetPath, false, iterations));
            }

            await MeasureMixAsync(volume, basePath, WorkDirectory(directory, "mix"), targetPath, iterations)
                .ConfigureAwait(false);
            var bursts = new List<BurstResult>();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                await using var session = await GrowthSession.OpenAsync(basePath,
                    WorkDirectory(directory, $"burst-{iteration}"), _timeProvider).ConfigureAwait(false);
                bursts.Add(await session.ApplyAsync(volume.BurstBatches(volume.Capacities.SlotCapacity))
                    .ConfigureAwait(false));
            }

            LogBurst(bursts);
            return 0;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    static string WorkDirectory(DirectoryInfo directory, string name) =>
        Directory.CreateDirectory(Path.Combine(directory.FullName, name)).FullName;

    /// <summary>
    ///     Fills the delta of a copy of the block with the measured mix through the journal path, then times a compacting
    ///     and a verbatim delta growth of the same source.
    /// </summary>
    async Task MeasureMixAsync(GrowthSyntheticVolume volume, string basePath, string workDirectory, string targetPath,
        int iterations)
    {
        await using var session = await GrowthSession.OpenAsync(basePath, workDirectory, _timeProvider).ConfigureAwait(false);
        var mix = await session.ApplyAsync(volume.MixBatches()).ConfigureAwait(false);
        if (mix.Growths.Length != 0 || mix.FinalDeltaEntries != volume.Capacities.DeltaCapacity)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The measured mix should fill the delta exactly: {mix.Growths.Length} growths, {mix.FinalDeltaEntries} of {volume.Capacities.DeltaCapacity} delta entries."));
        }

        _output.WriteLine(FormattableString.Invariant(
            $"Mix: {mix.Changes} name changes through the journal path, half creating a file under a new name on reused headroom records, half renaming a file to an existing name; the delta is full"));
        LogPhases("growth-compacting", MeasureGrowths(session.Block, targetPath, true, iterations));
        LogPhases("growth-mix-verbatim", MeasureGrowths(session.Block, targetPath, false, iterations));
    }

    /// <summary>One untimed warm-up growth, then <paramref name="iterations" /> measured ones.</summary>
    List<GrowthPhaseTimings> MeasureGrowths(BlockFile source, string targetPath, bool compacting, int iterations)
    {
        GrowthCopyPhases.Measure(source, targetPath, compacting, _timeProvider);
        return [.. Enumerable.Range(0, iterations)
            .Select(_ => GrowthCopyPhases.Measure(source, targetPath, compacting, _timeProvider))];
    }

    void LogGrowthVolume(GrowthSyntheticVolume volume)
    {
        var capacities = volume.Capacities;
        _output.WriteLine(FormattableString.Invariant(
            $"Synthetic block: {volume.Rows} rows, {volume.DistinctNames} distinct file names; {capacities.SlotCapacity} slots, {capacities.NameEntryCapacity} name entries, {capacities.NamePoolCapacity} name-pool bytes, {capacities.DeltaCapacity} delta entries"));
        _output.WriteLine(FormattableString.Invariant(
            $"Thresholds: compact at unused names >= 1/{WatchGrowthPlan.UnusedNameShareDenominator} of stored; double a name region above 1/{WatchGrowthPlan.FullNameRegionShareDenominator} full"));
        _output.WriteLine("Each growth is one delta growth composed from the copier's phases; one warm-up, then the median (minimum; maximum) of the runs");
    }

    void LogPhases(string label, List<GrowthPhaseTimings> runs)
    {
        var names = runs[0].Names;
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(FormattableString.Invariant(
            $"  Source names: {names.StoredEntries} stored, {names.ReferencedEntries} referenced, {names.UnusedEntries} unused ({Share(names.UnusedEntries, names.StoredEntries)}); pool bytes {names.StoredPoolBytes}, {names.CompactedPoolBytes} compacted"));
        _output.WriteLine($"  Default plan compacts this delta growth: {(runs[0].PlanCompacts ? "yes" : "no")}");
        LogMedian("Reference count", runs.Select(run => run.Count));
        LogMedian("Create replacement", runs.Select(run => run.Create));
        LogMedian("Copy rows", runs.Select(run => run.CopyRows));
        LogMedian("Copy names", runs.Select(run => run.CopyNames));
        LogMedian("Carry header", runs.Select(run => run.CarryHeader));
        LogMedian("Posting build", runs.Select(run => run.PostingBuild));
        LogMedian("Pause", runs.Select(run => run.Pause));
        LogMedian("Flush (off the pause)", runs.Select(run => run.Flush));
        if (runs[0].BucketRebuild is not null)
        {
            LogMedian("Bucket rebuild (replaces the bucket copy when the entry capacity changes)",
                runs.Select(run => run.BucketRebuild).OfType<double>());
        }

        _output.WriteLine($"  Bytes written: {runs[0].BytesWritten}");
    }

    void LogBurst(List<BurstResult> bursts)
    {
        var first = bursts[0];
        _output.WriteLine("--- growth-burst ---");
        _output.WriteLine(FormattableString.Invariant(
            $"  Creates: {first.Changes} through the journal path over the slot headroom, every other one under a new name"));
        _output.WriteLine(FormattableString.Invariant(
            $"  Growths: {first.Growths.Length}; bytes copied: {first.BytesCopied}; bytes copied per name change: {(double)first.BytesCopied / first.Changes:F1}"));
        foreach (var line in first.GrowthLines)
        {
            _output.WriteLine($"  {DescribeGrowth(line)}");
        }

        if (first.BatchPauses.Length == first.Growths.Length)
        {
            foreach (var growth in Enumerable.Range(0, first.Growths.Length))
            {
                LogMedian($"Growth {growth + 1} pause", bursts.Select(burst => burst.BatchPauses[growth]));
                LogMedian($"Growth {growth + 1} copy and posting build",
                    bursts.Select(burst => burst.Growths[growth].CopyMilliseconds));
                LogMedian($"Growth {growth + 1} renames and publication",
                    bursts.Select(burst => burst.Growths[growth].PublishMilliseconds));
            }
        }

        var final = first.Final;
        _output.WriteLine(FormattableString.Invariant(
            $"  Final: {final.SlotCapacity} slots, {final.NameEntryCapacity} name entries, {final.NamePoolCapacity} name-pool bytes, {final.DeltaCapacity} delta entries; capacity exhausted: {(first.CapacityExhausted ? "yes" : "no")}"));
        LogMedian("Total pause (growing batches beyond the median plain batch)", bursts.Select(burst => burst.PauseMilliseconds));
    }

    /// <summary>The part of a growth diagnostics line after the block path: the refused region, any compaction and the new capacities.</summary>
    internal static string DescribeGrowth(string line)
    {
        var start = line.IndexOf("': ", StringComparison.Ordinal);
        var end = line.IndexOf(". Retired", StringComparison.Ordinal);
        return start < 0 || end < start ? line : line[(start + 3)..end];
    }

    void LogMedian(string label, IEnumerable<double> samples)
    {
        var values = samples.ToArray();
        var median = Median(values, static (left, right) => (left + right) / 2);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {label} median: {median:F3} ms (minimum {values.Min():F3}; maximum {values.Max():F3}; n={values.Length})"));
    }

    static string Share(uint part, uint whole) =>
        whole == 0 ? "none stored" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * part / whole:F1}%");

    static bool TryParseGrowthArguments(string[] arguments, out uint rows, out int iterations)
    {
        rows = 0;
        iterations = 3;
        if ((arguments.Length != 2 && arguments.Length != 4) || arguments[0] != "--synthetic" ||
            !uint.TryParse(arguments[1], CultureInfo.InvariantCulture, out rows) ||
            rows is < GrowthSyntheticVolume.MinimumRows or > 20_000_000)
        {
            return false;
        }

        return arguments.Length == 2 || (arguments[2] == "--iterations" &&
                                         int.TryParse(arguments[3], CultureInfo.InvariantCulture, out iterations) &&
                                         iterations is >= 1 and <= 100);
    }
}
