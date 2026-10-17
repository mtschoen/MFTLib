using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using Benchmark;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class BenchmarkRunnerTests
{
    [TestMethod]
    [DataRow("--synthetic", "0")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "4294967295")]
    [DataRow("--unknown", "10")]
    [DataRow("--synthetic", "10", "--iterations", "0")]
    [DataRow("--synthetic", "10", "--iterations", "invalid")]
    [DataRow("--synthetic", "10", "--iterations")]
    [DataRow("--synthetic", "10", "--out")]
    [DataRow("--synthetic", "10", "--cache-directory", "other")]
    [DataRow("--cache-directory", "")]
    [DataRow("--synthetic")]
    [DataRow()]
    public async Task Run_Index_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index", .. arguments]));
        StringAssert.Contains(_output.ToString(), "Usage: Benchmark.exe index");
    }

    [TestMethod]
    public void PrintIndexMedian_UsesElapsedQueryTime()
    {
        const int iterations = 3;
        const string expected = "5.000";
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var output = new StringWriter();
        var runner = new BenchmarkRunner(output, clock);
        var durations = new Queue<double>([9, 1, 5]);
        runner.PrintIndexMedian("exact-search", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(durations.Dequeue()));
            return Array.Empty<FileEntry>();
        }, iterations, output.WriteLine);
        StringAssert.Contains(output.ToString(), $"Median: {expected} ms; Matches: 0");
        Assert.AreEqual(0, durations.Count);
    }

    [TestMethod]
    [DataRow(1, 3)]
    [DataRow(10, 4)]
    public async Task Run_Index_Synthetic_ReportsGeometryAndControlledMedians(int rows, int iterations)
    {
        Assert.AreEqual(0, await _runner.RunAsync(["INDEX", "--synthetic", rows.ToString(CultureInfo.InvariantCulture), "--iterations", iterations.ToString(CultureInfo.InvariantCulture)]));
        var output = _output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        StringAssert.Contains(output, $"Rows: {rows}");
        StringAssert.Contains(output, $"Slot capacity: {BlockLayout.ComputeSlotCapacity((uint)rows)}");
        StringAssert.Contains(output, "File bytes:");
        StringAssert.Contains(output, "Name-pool bytes:");
        StringAssert.Contains(output, "Name-pool capacity bytes:");
        StringAssert.Contains(output, "--- exact-search ---");
        StringAssert.Contains(output, "--- substring-search ---");
        StringAssert.Contains(output, $"Matches: {(rows == 1 ? 0 : 1)}");
    }

    [TestMethod]
    public async Task Run_Index_DefaultIterations_ReportsThreeSamples()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        StringAssert.Contains(_output.ToString(), "median of 3 runs");
    }

    [TestMethod]
    public async Task Run_Index_CacheDirectory_UsesEachStoredTagAndLeavesBlocksIntact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var drive in new[] { 'T', 'U' })
            {
                using var block = BlockFile.Create(new BlockFileCreateOptions
                {
                    Path = Path.Combine(directory, $"{drive}-00000001.mlix"),
                    VolumeSerial = 1,
                    ProducerKind = ProducerKind.Enumeration,
                    SlotCapacity = 8,
                    NamePoolCapacity = 4096,
                    CacheTag = new CacheTag("TEST", drive)
                });
                var writer = new BlockWriter(block);
                Assert.IsTrue(writer.TryWriteRow(0, directory, new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0)));
                Assert.IsTrue(writer.TryWriteRow(1, "file-0000000001", new RowColumns(0, RowFlags.InUse, 0, 1, 0, 0)));
                writer.Complete(DateTime.UtcNow, null);
            }
            var originals = Directory.GetFiles(directory, "*.mlix").ToDictionary(path => path, File.ReadAllBytes);
            Assert.AreEqual(0, await _runner.RunAsync(["index", "--cache-directory", directory]));
            var output = _output.ToString();
            StringAssert.Contains(output, "Drive: T");
            StringAssert.Contains(output, "Drive: U");
            StringAssert.Contains(output, "Rows: 2");
            StringAssert.Contains(output, "Matches: 1");
            var usedNameBytes = (directory.Length + "file-0000000001".Length) * 2;
            var fileBytes = new FileInfo(originals.Keys.First()).Length;
            StringAssert.Contains(output, string.Create(CultureInfo.InvariantCulture,
                $"Name-pool bytes: {usedNameBytes} ({100.0 * usedNameBytes / fileBytes:F2}% of file)"));
            foreach (var original in originals)
            {
                CollectionAssert.AreEqual(original.Value, await File.ReadAllBytesAsync(original.Key));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_Index_EmptyOrCorruptCache_FailsWithoutScanning(bool corrupt)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            if (corrupt)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "T-00000001.mlix"), "corrupt");
            }
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            StringAssert.Contains(_output.ToString(), corrupt ? "Invalid" : "No cache blocks");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_Index_OfflineOrLockedCache_ReportsUnavailable(bool locked)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "T-00000001.mlix");
        try
        {
            using (var block = BlockFile.Create(new BlockFileCreateOptions
            {
                Path = path,
                VolumeSerial = 1,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = 8,
                NamePoolCapacity = 4096
            }))
            {
                var writer = new BlockWriter(block);
                writer.TryWriteRow(0, Path.Combine(directory, "missing-root"),
                    new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                writer.Complete(DateTime.UtcNow, null);
            }
            using var owner = locked ? BlockOwnerLock.TryAcquire(path) : null;
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            StringAssert.Contains(_output.ToString(), locked ? "InUse" : "Offline");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Run_Index_MftCache_OpensWithJournalIsolationEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("An MFT block names a drive letter, and only Windows has a drive root to open it under.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var drive = Path.GetPathRoot(directory)![0];
        try
        {
            using (var block = BlockFile.Create(new BlockFileCreateOptions
            {
                Path = Path.Combine(directory, $"{drive}-00000001.mlix"),
                VolumeSerial = 1,
                ProducerKind = ProducerKind.Mft,
                RootRow = 5,
                SlotCapacity = 8,
                NamePoolCapacity = 4096
            }))
            {
                var writer = new BlockWriter(block);
                writer.TryWriteRow(5, "", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                block.Header.UsnJournalIdentifier = 123;
                block.Header.UsnNextUsn = 456;
                writer.Complete(DateTime.UtcNow, null);
            }
            Assert.AreEqual(0, await _runner.RunAsync(["index", "--cache-directory", directory]),
                _output.ToString());
            StringAssert.Contains(_output.ToString(), "Rows: 6");
            StringAssert.Contains(_output.ToString(), "Name-pool bytes: 0 (0.00% of file)");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Run_NoVerb_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync([]));
        StringAssert.Contains(_output.ToString(), "Usage: Benchmark.exe <index|compare>");
    }

    [TestMethod]
    public async Task Run_Index_PrintsSystemInfo()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        var output = _output.ToString();
        StringAssert.Contains(output, "System Info");
        StringAssert.Contains(output, "CPU:");
        StringAssert.Contains(output, "Disk:");
    }

    [TestMethod]
    public async Task Run_Index_WithoutOut_WritesNoFile()
    {
        var beforeFiles = Directory.GetFiles(_directory);
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        CollectionAssert.AreEqual(beforeFiles, Directory.GetFiles(_directory));
        Assert.IsFalse(_output.ToString().Contains("Report saved to", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Run_Index_Failure_WritesNoReport()
    {
        var report = Path.Combine(_directory, "report.txt");
        await File.WriteAllTextAsync(report, "keep me");
        var emptyCache = Directory.CreateDirectory(Path.Combine(_directory, "empty-cache")).FullName;
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", emptyCache, "--out", report]));
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(report));
    }

    [TestMethod]
    public async Task Run_Index_WriteFailure_ReportsError()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--synthetic", "2", "--out", _directory]));
        StringAssert.Contains(_output.ToString(), "Error:");
    }

    [TestMethod]
    public async Task Run_OutputIsBorrowedAndReportsAreRealFiles()
    {
        using var output = new StringWriter();
        var runner = new BenchmarkRunner(output);
        var path = Path.Combine(_directory, "report with spaces.txt");
        Assert.AreEqual(0, await runner.RunAsync(["index", "--synthetic", "2", "--out", path]));
        var saved = await File.ReadAllTextAsync(path);
        StringAssert.Contains(saved, "--- exact-search ---");
        StringAssert.Contains(saved, "--- substring-search ---");
        StringAssert.Contains(output.ToString(), $"Report saved to {Path.GetFullPath(path)}");
        Assert.AreEqual(0, await runner.RunAsync(["compare", path, path, "exact-search"]));
        await output.WriteLineAsync("still owned by caller");
        StringAssert.Contains(output.ToString(), "still owned by caller");
    }

    [TestMethod]
    public async Task Compare_IndexReportAgainstItself_Passes_AndGrowthBeyondTheBoundFails()
    {
        var baselinePath = Path.Combine(_directory, "baseline.txt");
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2", "--out", baselinePath]));

        foreach (var scenario in new[] { "exact-search", "substring-search" })
        {
            Assert.AreEqual(0, await _runner.RunAsync(["compare", baselinePath, baselinePath, scenario]));
        }

        var literalBaseline = Path.Combine(_directory, "literal-baseline.txt");
        var grownPath = Path.Combine(_directory, "grown.txt");
        await File.WriteAllTextAsync(literalBaseline, "--- exact-search ---\n  Median: 4.000 ms; Matches: 1\n--- substring-search ---\n  Median: 4.000 ms; Matches: 1");
        await File.WriteAllTextAsync(grownPath, "--- exact-search ---\n  Median: 5.001 ms; Matches: 1\n--- substring-search ---\n  Median: 5.001 ms; Matches: 1");

        foreach (var scenario in new[] { "exact-search", "substring-search" })
        {
            Assert.AreEqual(1, await _runner.RunAsync(["compare", literalBaseline, grownPath, scenario]));
        }

        StringAssert.Contains(_output.ToString(), "FAILED");
    }

    [TestMethod]
    [DataRow("4.000", "5.000", 0)]
    [DataRow("0.020", "0.025", 0)]
    [DataRow("4.000", "5.001", 1)]
    [DataRow("0.020", "0.026", 1)]
    public async Task Compare_GrowthAgainstTheBound(string before, string after, int expectedExitCode)
    {
        Assert.AreEqual(expectedExitCode, await Compare($"--- exact-search ---\n  Median: {before} ms; Matches: 1",
            $"--- exact-search ---\n  Median: {after} ms; Matches: 1"));
    }

    [TestMethod]
    public async Task Compare_SpeedupAndZeroBaseline()
    {
        Assert.AreEqual(0, await Compare("--- exact-search ---\n  Median: 4.000 ms", "--- exact-search ---\n  Median: 1.000 ms"));
        Assert.AreEqual(0, await Compare("--- exact-search ---\n  Median: 0.000 ms", "--- exact-search ---\n  Median: 0.000 ms"));
        Assert.AreEqual(1, await Compare("--- exact-search ---\n  Median: 0.000 ms", "--- exact-search ---\n  Median: 0.001 ms"));
    }

    [TestMethod]
    public async Task Compare_ReadsTheNamedScenarioBlock()
    {
        const string report = "--- exact-search ---\r\n  Median: 1.000 ms\r\n--- substring-search ---\r\n  Median: 9.000 ms";
        Assert.AreEqual(0, await Compare(report, report.Replace("1.000", "1.100", StringComparison.Ordinal)));
        Assert.AreEqual(1, await Compare(report, report.Replace("9.000", "99.000", StringComparison.Ordinal), "substring-search"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("--- exact-search ---\n  Matches: 1")]
    [DataRow("--- other ---\n  Median: 1.000 ms")]
    public async Task Compare_MissingScenarioOrMedian_ReturnsOne(string afterReport)
    {
        Assert.AreEqual(1, await Compare("--- exact-search ---\n  Median: 1.000 ms", afterReport));
        StringAssert.Contains(_output.ToString(), "no median for scenario 'exact-search'");
    }

    [TestMethod]
    public async Task Compare_MissingFiles_ReturnsOne()
    {
        var before = Path.Combine(_directory, "before.txt");
        var after = Path.Combine(_directory, "after.txt");
        Assert.AreEqual(1, await _runner.RunAsync(["compare", before, after, "exact-search"]));
        StringAssert.Contains(_output.ToString(), $"Baseline before file not found: {before}");
        await File.WriteAllTextAsync(before, "--- exact-search ---\n  Median: 1.000 ms");
        Assert.AreEqual(1, await _runner.RunAsync(["compare", before, after, "exact-search"]));
        StringAssert.Contains(_output.ToString(), $"Benchmark after file not found: {after}");
    }

    [TestMethod]
    public async Task Compare_MissingArguments_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["compare", "before.txt", "after.txt"]));
        StringAssert.Contains(_output.ToString(), "Usage: Benchmark.exe compare");
    }

    [TestMethod]
    public void Benchmark_Sources_HaveNoRetiredRecordArrayScenarios()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Benchmark", "Benchmark.csproj")))
        {
            root = root.Parent;
        }

        Assert.IsNotNull(root, "Benchmark sources were not found above the test output directory.");
        var retired = new System.Text.RegularExpressions.Regex("compat|ToArray", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = Directory.EnumerateFiles(Path.Combine(root.FullName, "Benchmark"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path).Where(line => retired.IsMatch(line)).Select(line => $"{Path.GetFileName(path)}: {line.Trim()}"));
        CollectionAssert.AreEqual(Array.Empty<string>(), matches.ToArray());
    }

    async Task<int> Compare(string beforeReport, string afterReport, string scenario = "exact-search")
    {
        var before = Path.Combine(_directory, "before.txt");
        var after = Path.Combine(_directory, "after.txt");
        await File.WriteAllTextAsync(before, beforeReport);
        await File.WriteAllTextAsync(after, afterReport);
        return await _runner.RunAsync(["compare", before, after, scenario]);
    }


    string _directory = null!;
    StringWriter _output = null!;
    BenchmarkRunner _runner = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("benchmark-host-").FullName;
        _output = new StringWriter();
        _runner = new BenchmarkRunner(_output);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _output.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
