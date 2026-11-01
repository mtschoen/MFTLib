using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BenchmarkRunnerTests
{
    [TestMethod]
    public async Task Run_IndexWrite_ReportsIsolatedProductionTransfer()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index-write", "--synthetic", "200", "--iterations", "1", "--unique-names", "20"]), _output.ToString());
        var report = _output.ToString();
        Assert.Contains("Input records: 200; unique names: 20", report);
        Assert.Contains("Production transfer median:", report);
        Assert.Contains("Name interning median:", report);
        Assert.Contains("Empty-name row write median:", report);
        Assert.Contains("Access scope median:", report);
        Assert.Contains("UTF-16 append median:", report);
        Assert.Contains("Live rows: 200; name entries: 20", report);
        Assert.Contains("Peak private bytes:", report);
        Assert.Contains("Peak working set bytes:", report);
    }

    [TestMethod]
    [DataRow("0", "1", "1")]
    [DataRow("200", "0", "20")]
    [DataRow("200", "1", "201")]
    [DataRow("invalid", "1", "20")]
    public async Task Run_IndexWrite_InvalidArguments_ReportUsage(string records, string iterations, string names)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index-write", "--synthetic", records, "--iterations", iterations, "--unique-names", names]));
        Assert.Contains("Usage: Benchmark.exe index-write", _output.ToString());
    }
}
