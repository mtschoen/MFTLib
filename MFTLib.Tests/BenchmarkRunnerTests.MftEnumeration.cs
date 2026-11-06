using System.Globalization;
using System.Text.RegularExpressions;
using Benchmark;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BenchmarkRunnerTests
{
    static readonly string[] MftEnumerationBlocks =
        ["mft-enumeration-scan", "mft-enumeration-rows", "mft-enumeration-exact-name", "mft-enumeration-substring-name"];

    [TestMethod]
    [DataRow("--synthetic", "15")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "10000001")]
    [DataRow("--dump", "")]
    [DataRow("--dump", " ")]
    [DataRow("--synthetic", "64", "--dump", "volume.mft")]
    [DataRow("--iterations", "0")]
    [DataRow("--iterations", "invalid")]
    [DataRow("--iterations", "2", "--iterations", "3")]
    [DataRow("--out", " ")]
    [DataRow("--out")]
    [DataRow("--unknown", "1")]
    public async Task Run_MftEnumeration_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["mft-enumeration", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe mft-enumeration", _output.ToString());
    }

    [TestMethod]
    public async Task Run_MftEnumeration_Synthetic_ReportsEveryBlockAndDeletesTheGeneratedDump()
    {
        const int iterations = 3;
        var memoryInputs = new List<string>();
        await using var output = new StringWriter();
        var runner = new BenchmarkRunner(output, new SteppingTimeProvider(TimeSpan.FromMilliseconds(2)), request =>
        {
            Assert.IsTrue(File.Exists(request.InputPath), "Each memory worker reads the generated dump.");
            memoryInputs.Add(request.InputPath);
            return FixedMemoryAsync(request);
        });
        var reportPath = Path.Combine(_directory, "mft report.txt");

        Assert.AreEqual(0, await runner.RunAsync(["MFT-ENUMERATION", "--synthetic", "4096",
            "--iterations", iterations.ToString(CultureInfo.InvariantCulture), "--out", reportPath]), output.ToString());

        var report = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken);
        Assert.StartsWith(report, output.ToString());
        Assert.Contains($"Commit: {BenchmarkRunner.GetBuildCommit()}", report);
        Assert.Contains("synthetic dump from the native bulk generator 'synthetic.mft': 4096 records of 1024 bytes", report);
        Assert.Contains($"the median of {iterations}", report);
        // Each timed interval reads the stepping clock twice, so every median is one step.
        Assert.MatchesRegex(@"--- mft-enumeration-scan ---\n  Median: 2\.000 ms; Records: 4096; Slots: [1-9][0-9]*; Live rows: [1-9][0-9]*\n", Normalize(report));
        Assert.AreEqual(4, Regex.Count(report, @"(?m)^  Median: 2\.000 ms; Records: [1-9][0-9]*;"));
        Assert.Contains("Records per second: 2048000", report);
        Assert.MatchesRegex(@"--- mft-enumeration-exact-name ---\r?\n  Median: 2\.000 ms; Records: [0-9]+; Matches: [1-9]", report);
        Assert.MatchesRegex(@"--- mft-enumeration-substring-name ---\r?\n  Median: 2\.000 ms; Records: [0-9]+; Matches: [1-9]", report);
        Assert.AreEqual(4, Regex.Count(report, @"(?m)^  Peak private bytes: 200\r?$"));
        Assert.AreEqual(4, Regex.Count(report, @"(?m)^  Memory measurement: windows-os-peak\r?$"));

        Assert.HasCount(MftEnumerationBlocks.Length, memoryInputs);
        Assert.AreEqual(1, memoryInputs.Distinct().Count());
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(memoryInputs[0])), "The generated dump is deleted.");
        foreach (var scenario in MftEnumerationBlocks)
        {
            Assert.AreEqual(0, await runner.RunAsync(["compare", reportPath, reportPath, scenario]), output.ToString());
        }
    }

    [TestMethod]
    public async Task Run_MftEnumeration_SavedDump_CountsTheFixtureMatchesAndKeepsTheDump()
    {
        var records = MftDumpFixture.StandardRecords();
        records[11] = new DumpRecord(".git", 5) { IsDirectory = true };
        records[12] = new DumpRecord("config", 11) { Size = 5 };
        records[13] = new DumpRecord("app.CONFIG.json", 6) { Size = 7 };
        var dump = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Build(1024, 16, records));

        Assert.AreEqual(0, await _runner.RunAsync(["mft-enumeration", "--dump", dump, "--iterations", "1"]),
            _output.ToString());

        var output = _output.ToString();
        Assert.Contains("saved dump 'volume.mft': 16 records of 1024 bytes", output);
        // Records 0 to 13 give fourteen slots; ten of them are in use with a name.
        Assert.MatchesRegex(@"Records: 16; Slots: 14; Live rows: 10\r?\n", output);
        Assert.MatchesRegex(@"--- mft-enumeration-exact-name ---\r?\n  Median: [0-9.]+ ms; Records: [0-9]+; Matches: 1\r?\n", output);
        Assert.MatchesRegex(@"--- mft-enumeration-substring-name ---\r?\n  Median: [0-9.]+ ms; Records: [0-9]+; Matches: 2\r?\n", output);
        Assert.IsTrue(File.Exists(dump), "A caller's dump is never deleted.");
    }

    [TestMethod]
    public async Task Run_MftEnumeration_MemoryFailure_ReportsErrorKeepsReportAndDeletesTheGeneratedDump()
    {
        var reportPath = Path.Combine(_directory, "report.txt");
        await File.WriteAllTextAsync(reportPath, "keep me", TestContext.CancellationToken);
        string? input = null;
        var runner = new BenchmarkRunner(_output, TimeProvider.System, request =>
        {
            input = request.InputPath;
            throw new InvalidOperationException("injected memory failure");
        });

        Assert.AreEqual(1, await runner.RunAsync(["mft-enumeration", "--synthetic", "64", "--iterations", "1",
            "--out", reportPath]));

        Assert.Contains("Error: injected memory failure", _output.ToString());
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken));
        Assert.IsNotNull(input);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(input)), "The generated dump is deleted on failure.");
    }

    [TestMethod]
    public async Task Run_MftEnumeration_MissingOrInvalidDump_ReportsError()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["mft-enumeration", "--dump", Path.Combine(_directory, "absent.mft")]));
        Assert.Contains("Error:", _output.ToString());

        var invalid = MftDumpFixture.Standard();
        invalid[(7 * 1024) + 510] ^= 0xFF;
        var dump = MftDumpFixture.WriteFile(_directory, invalid);
        Assert.AreEqual(1, await _runner.RunAsync(["mft-enumeration", "--dump", dump]));
        Assert.Contains("Error: Root T: Failed (The dump contains an invalid MFT record fixup.).", _output.ToString());
    }

    [TestMethod]
    public async Task MeasureMemory_MftEnumeration_RunsEachScenarioInAWorkerAndRejectsInvalidRequests()
    {
        var dump = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        foreach (var scenario in MftEnumerationBlocks)
        {
            var result = await ScenarioMemoryProcess.MeasureAsync(
                new ScenarioMemoryRequest(scenario, dump, 1), TestContext.CancellationToken);
            Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
            Assert.IsGreaterThan(0, result.PeakPrivateBytes);
        }

        foreach (var payload in new[]
        {
            "{\"Scenario\":\"mft-enumeration-scan\",\"InputPath\":\"absent.mft\",\"Iterations\":1}",
            $"{{\"Scenario\":\"mft-enumeration-rows\",\"InputPath\":{System.Text.Json.JsonSerializer.Serialize(dump)},\"Iterations\":1,\"Rows\":1}}"
        })
        {
            using var workerOut = new StringWriter();
            using var workerError = new StringWriter();
            Assert.AreEqual(1, await BenchmarkRunner.RunMemoryWorkerAsync(new StringReader(payload), workerOut, workerError));
            Assert.StartsWith("Error: Invalid MFT enumeration memory request.", workerError.ToString());
        }
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>A clock that moves one fixed step on every read, so an interval between two reads is exactly one step.</summary>
    sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);
    }
}
