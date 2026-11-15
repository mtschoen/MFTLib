using Benchmark;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BenchmarkRunnerTests
{
    /// <summary>
    ///     The growth scenario at its smallest useful size, on a clock that moves two milliseconds per read: every phase is
    ///     one interval, so the pause is the six pause phases. A plain batch reads the clock twice; the growing batch also
    ///     reads it at three growth steps, so its pause beyond the median plain batch is three intervals.
    /// </summary>
    [TestMethod]
    public async Task Run_IndexGrowth_TimesEveryPhaseOfEachRunAndTheBurst()
    {
        using var output = new StringWriter();
        var runner = new BenchmarkRunner(output, new SteppingTimeProvider(TimeSpan.FromMilliseconds(2)));

        Assert.AreEqual(0, await runner.RunAsync(["index-growth", "--synthetic", "200", "--iterations", "1"]), output.ToString());

        var report = Normalize(output.ToString());
        Assert.Contains("Synthetic block: 200 rows, 97 distinct file names; 65736 slots, 65736 name entries, 1581056 name-pool bytes, 65536 delta entries", report);
        Assert.Contains("Thresholds: compact at unused names >= 1/8 of stored; double a name region above 1/2 full", report);
        foreach (var label in new[] { "growth-verbatim", "growth-compacting", "growth-mix-verbatim" })
        {
            var block = ScenarioBlock(report, label);
            foreach (var phase in new[] { "Reference count", "Create replacement", "Copy rows", "Copy names", "Carry header", "Posting build", "Flush (off the pause)" })
            {
                Assert.Contains($"  {phase} median: 2.000 ms (minimum 2.000; maximum 2.000; n=1)", block, label);
            }

            Assert.Contains("  Pause median: 12.000 ms", block, label);
            Assert.Contains("  Bytes written: ", block, label);
        }

        var verbatim = ScenarioBlock(report, "growth-verbatim");
        Assert.Contains("  Source names: 98 stored, 98 referenced, 0 unused (0.0%)", verbatim);
        Assert.Contains("  Default plan compacts this delta growth: no", verbatim);
        Assert.Contains("  Bucket rebuild (replaces the bucket copy when the entry capacity changes) median: 2.000 ms", verbatim);
        var compacting = ScenarioBlock(report, "growth-compacting");
        Assert.Contains("  Default plan compacts this delta growth: yes", compacting);
        Assert.DoesNotContain("Bucket rebuild", compacting);
        Assert.Contains("Mix: 65536 name changes through the journal path", report);
        var burst = ScenarioBlock(report, "growth-burst");
        Assert.Contains("  Creates: 65736 through the journal path", burst);
        Assert.Contains("  Growths: 1; bytes copied: ", burst);
        Assert.Contains("  the journal record at USN 69633 (record 200) was refused by the Delta region; the watch continues with 65736 slots, 65736 name entries, 1581056 name-pool bytes and 65736 delta entries", burst);
        Assert.Contains("  Final: 65736 slots, 65736 name entries, 1581056 name-pool bytes, 65736 delta entries; capacity exhausted: no", burst);
        Assert.Contains("  Growth 1 pause median: 6.000 ms", burst);
        Assert.Contains("  Growth 1 copy and posting build median: 2.000 ms", burst);
        Assert.Contains("  Growth 1 renames and publication median: 2.000 ms", burst);
        Assert.Contains("  Total pause (growing batches beyond the median plain batch) median: 6.000 ms", burst);
    }

    [TestMethod]
    [DataRow("--synthetic", "9")]
    [DataRow("--synthetic", "20000001")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "200", "--iterations", "0")]
    [DataRow("--synthetic", "200", "--iterations", "101")]
    [DataRow("--synthetic", "200", "--rounds", "1")]
    [DataRow("--rows", "200")]
    [DataRow("--synthetic")]
    [DataRow]
    public async Task Run_IndexGrowth_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index-growth", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe index-growth --synthetic N", _output.ToString());
    }

    [TestMethod]
    public void GrowthPauses_AreTheExcessOfEachGrowingBatchOverTheMedianPlainBatch()
    {
        CollectionAssert.AreEqual(new[] { 9.5, 0.0 }, GrowthSession.GrowthPauses([(2, 0), (4, 0), (13, 1), (1, 1), (3, 0), (5, 0)]));
        CollectionAssert.AreEqual(new[] { 7.0 }, GrowthSession.GrowthPauses([(3, 0), (10, 2)]));
        CollectionAssert.AreEqual(new[] { 0.0 }, GrowthSession.GrowthPauses([(10, 1)]));
    }

    [TestMethod]
    public void DescribeGrowth_KeepsTheRefusalAndCapacities_OrTheWholeLineWhenUnrecognized()
    {
        Assert.AreEqual("refused by the Delta region; the watch continues with 8 slots",
            BenchmarkRunner.DescribeGrowth("Grew block file 'T.mlix': refused by the Delta region; the watch continues with 8 slots. Retired mappings hold 0 bytes."));
        Assert.AreEqual("Grew without a path", BenchmarkRunner.DescribeGrowth("Grew without a path"));
    }

    static string ScenarioBlock(string report, string label)
    {
        var start = report.IndexOf($"--- {label} ---", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, label);
        var end = report.IndexOf("--- ", start + label.Length + 8, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }
}
