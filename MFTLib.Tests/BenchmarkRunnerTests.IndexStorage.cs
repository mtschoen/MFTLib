using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BenchmarkRunnerTests
{
    [TestMethod]
    public async Task Run_IndexPipeline_ReportsCompletionAndBothRowPasses_Issue882()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index-pipeline", "--synthetic", "200", "--distinct-names", "20", "--iterations", "5"]), _output.ToString());
        var output = _output.ToString();
        Assert.Contains("Rows: 200; Distinct names: 20; Iterations: 5", output);
        Assert.Contains("--- block-completion ---", output);
        Assert.Contains("--- posting-rebuild ---", output);
        Assert.Contains("--- rows-metadata ---", output);
        Assert.Contains("--- rows-names ---", output);
        Assert.Contains("--- rows-paths ---", output);
        Assert.Contains("Path allocated bytes:", output);
        Assert.Contains("Checksum: 200", output);
        Assert.Contains("Name Checksum: -8998155903086285788", output);
        Assert.Contains("Peak private bytes:", output);
        Assert.Contains("Peak working set:", output);
    }

    [TestMethod]
    [DataRow]
    [DataRow("--synthetic")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "100000001")]
    [DataRow("--synthetic", "0")]
    [DataRow("--synthetic", "200", "--iterations")]
    [DataRow("--synthetic", "200", "--iterations", "invalid")]
    [DataRow("--synthetic", "200", "--iterations", "1", "--iterations", "2")]
    [DataRow("--synthetic", "200", "--distinct-names", "invalid")]
    [DataRow("--synthetic", "200", "--distinct-names", "201")]
    [DataRow("--synthetic", "200", "--iterations", "0")]
    [DataRow("--synthetic", "200", "--distinct-names", "0")]
    [DataRow("--synthetic", "200", "--unknown", "5")]
    public async Task Run_IndexPipeline_RejectsInvalidArguments(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index-pipeline", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe index-pipeline", _output.ToString());
    }

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
