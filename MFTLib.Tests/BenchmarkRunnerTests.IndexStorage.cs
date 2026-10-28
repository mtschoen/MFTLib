using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BenchmarkRunnerTests
{
    [TestMethod]
    public async Task Run_IndexStorage_ReportsProductionWritesAndNameRegionUsage()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index-storage", "--synthetic", "200"]), _output.ToString());
        var output = _output.ToString();
        Assert.Contains("Input records: 200", output);
        Assert.Contains("Live rows: 200", output);
        Assert.Contains("Name entries: 198", output);
        Assert.Contains("Name-pool used bytes: 3909", output);
        Assert.Contains("Name-entry used bytes: 2376", output);
        Assert.Contains("Name entry capacity: ", output);
        Assert.Contains("Bucket bytes: 131072", output);
        Assert.Contains("Scan write time:", output);
        Assert.Contains("Skipped records: 0", output);
    }

    [TestMethod]
    [DataRow("--synthetic", "0")]
    [DataRow("--synthetic", "5")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "100000001")]
    [DataRow("--synthetic", "200", "--keep-name", ".git")]
    [DataRow("--unknown", "200")]
    public async Task Run_IndexStorage_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index-storage", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe index-storage", _output.ToString());
    }

    [TestMethod]
    public async Task Run_IndexStorage_WithoutSyntheticCount_PrintsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index-storage"]));
        Assert.Contains("Usage: Benchmark.exe index-storage --synthetic N", _output.ToString());
    }
}
