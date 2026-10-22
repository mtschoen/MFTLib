using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Benchmark;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public partial class BenchmarkRunnerTests
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
    [DataRow]
    public async Task Run_Index_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe index", _output.ToString());
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
        Assert.Contains($"Median: {expected} ms; Matches: 0", output.ToString());
        Assert.IsEmpty(durations);
    }

    [TestMethod]
    [DataRow(1, 3)]
    [DataRow(10, 4)]
    public async Task Run_Index_Synthetic_ReportsGeometryAndControlledMedians(int rows, int iterations)
    {
        Assert.AreEqual(0, await _runner.RunAsync(["INDEX", "--synthetic", rows.ToString(CultureInfo.InvariantCulture), "--iterations", iterations.ToString(CultureInfo.InvariantCulture)]));
        var output = _output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains($"Rows: {rows}", output);
        Assert.Contains($"Slot capacity: {BlockLayout.ComputeSlotCapacity((uint)rows)}", output);
        Assert.Contains("File bytes:", output);
        Assert.Contains("Name-pool bytes:", output);
        Assert.Contains("Name-pool capacity bytes:", output);
        Assert.Contains("--- exact-search ---", output);
        Assert.Contains("--- substring-search ---", output);
        Assert.Contains($"Matches: {(rows == 1 ? 0 : 1)}", output);
    }

    [TestMethod]
    [DataRow("--synthetic", "15")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "10000001")]
    [DataRow("--dump", "")]
    [DataRow("--synthetic", "64", "--iterations", "0")]
    [DataRow("--unknown", "64")]
    [DataRow]
    public async Task Run_NativeTimings_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["native-timings", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe native-timings", _output.ToString());
    }

    [TestMethod]
    public async Task Run_NativeTimings_Synthetic_PrintsTheIoFixupParseTotalBreakdown()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["native-timings", "--synthetic", "4096", "--iterations", "2"]));
        var output = _output.ToString();
        Assert.Contains("--- native-parse-timings ---", output);
        Assert.Contains("Records: 4096 examined", output);
        Assert.Contains("(median of 2 runs)", output);
        var milliseconds = new Dictionary<string, double>();
        foreach (var label in new[] { "Native I/O", "Native fixup", "Native parse", "Native total" })
        {
            var match = Regex.Match(output, Regex.Escape(label) + @":\s+([0-9.]+) ms");
            Assert.IsTrue(match.Success, $"{label} line is present.");
            milliseconds[label] = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        foreach (var label in new[] { "Native I/O", "Native fixup", "Native parse" })
        {
            Assert.IsGreaterThanOrEqualTo(milliseconds[label], milliseconds["Native total"], $"The total bounds {label}.");
        }
        Assert.MatchesRegex(MyRegex(), output);
        Assert.Contains("buffered", output);
        Assert.DoesNotContain("disk is the bottleneck", output, "The verdict names a stage, not the disk.");
    }

    [TestMethod]
    public async Task Run_NativeTimings_MissingDump_ReportsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mft");
        Assert.AreEqual(1, await _runner.RunAsync(["native-timings", "--dump", path]));
        Assert.Contains("Error:", _output.ToString());
    }

    [TestMethod]
    public async Task Run_Index_DefaultIterations_ReportsThreeSamples()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        Assert.Contains("median of 3 runs", _output.ToString());
    }

    [TestMethod]
    public async Task Run_Index_CacheDirectory_UsesEachStoredTagAndLeavesBlocksIntact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            void WriteBlock(char drive, ulong scanShape, ProducerKind producer = ProducerKind.Enumeration) =>
                WriteCacheBlock(directory, directory, drive, scanShape, producer);

            WriteBlock('T', SyntheticScanShape.Default(ProducerKind.Enumeration));
            WriteBlock('U', SyntheticScanShape.Default(ProducerKind.Enumeration));

            // One block per row-shaping option the benchmark does not supply (issue #678): each must
            // be declined as an unsupported scan shape and its bytes left intact.
            var declined = new (char Drive, ulong Shape, ProducerKind Producer)[]
            {
                ('V', ScanShape.ForEnumeration(["child.txt"], new DirectoryWalkOptions(), directory), ProducerKind.Enumeration),
                ('W', ScanShape.ForEnumeration([], new DirectoryWalkOptions(), directory), ProducerKind.Enumeration),
                ('X', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryNames = ["build"] }, directory), ProducerKind.Enumeration),
                ('Y', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(directory, "build")] }, directory), ProducerKind.Enumeration),
                ('Z', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryAttributes = FileAttributes.Hidden }, directory), ProducerKind.Enumeration),
                ('S', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PruneDotDirectories = true }, directory), ProducerKind.Enumeration),
                ('M', ScanShape.ForMft(["child.txt"], includeFreed: false), ProducerKind.Mft),
                ('N', ScanShape.ForMft(null, includeFreed: true), ProducerKind.Mft)
            };
            foreach (var (drive, shape, producer) in declined)
            {
                WriteBlock(drive, shape, producer);
            }

            var originals = Directory.GetFiles(directory, "*.mlix").ToDictionary(path => path, File.ReadAllBytes);
            Assert.HasCount(2 + declined.Length, originals);
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            var output = _output.ToString();
            Assert.Contains("Drive: T", output);
            Assert.Contains("Drive: U", output);
            Assert.Contains("Rows: 2", output);
            Assert.Contains("Matches: 1", output);
            var usedNameBytes = (directory.Length + "file-0000000001".Length) * 2;
            var fileBytes = new FileInfo(Path.Combine(directory, "T-00000001.mlix")).Length;
            Assert.Contains(string.Create(CultureInfo.InvariantCulture,
                $"Name-pool bytes: {usedNameBytes} ({100.0 * usedNameBytes / fileBytes:F2}% of file)"), output);
            foreach (var (drive, _, _) in declined)
            {
                Assert.Contains($"{drive}-00000001.mlix: unsupported scan shape", output);
            }
            foreach (var original in originals)
            {
                Assert.AreSequenceEqual(original.Value, await File.ReadAllBytesAsync(original.Key, TestContext.CancellationToken));
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
                await File.WriteAllTextAsync(Path.Combine(directory, "T-00000001.mlix"), "corrupt", TestContext.CancellationToken);
            }
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            Assert.Contains(corrupt ? "Invalid" : "No cache blocks", _output.ToString());
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
                NamePoolCapacity = 4096,
                ScanShape = SyntheticScanShape.Default(ProducerKind.Enumeration)
            }))
            {
                var writer = new BlockWriter(block);
                writer.TryWriteRow(0, Path.Combine(directory, "missing-root"),
                    new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                writer.Complete(DateTime.UtcNow, null);
            }
            using var owner = locked ? BlockOwnerLock.TryAcquire(path) : null;
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            Assert.Contains(locked ? "InUse" : "Offline", _output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task Run_Index_MftCache_OpensWithJournalIsolationEnabled()
    {
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
                NamePoolCapacity = 4096,
                ScanShape = SyntheticScanShape.Default(ProducerKind.Mft)
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
            Assert.Contains("Rows: 6", _output.ToString());
            Assert.Contains("Name-pool bytes: 0 (0.00% of file)", _output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Run_NoVerb_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync([]));
        Assert.Contains("Usage: Benchmark.exe <index|compare|native-timings|enumeration-walk>", _output.ToString());
    }

    [TestMethod]
    public async Task Run_Index_PrintsSystemInfo()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        var output = _output.ToString();
        Assert.Contains("System Info", output);
        Assert.Contains("CPU:", output);
        Assert.Contains("Disk:", output);
    }

    [TestMethod]
    public async Task Run_Index_WithoutOut_WritesNoFile()
    {
        var beforeFiles = Directory.GetFiles(_directory);
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        Assert.AreSequenceEqual(beforeFiles, Directory.GetFiles(_directory));
        Assert.IsFalse(_output.ToString().Contains("Report saved to", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Run_Index_Failure_WritesNoReport()
    {
        var report = Path.Combine(_directory, "report.txt");
        await File.WriteAllTextAsync(report, "keep me", TestContext.CancellationToken);
        var emptyCache = Directory.CreateDirectory(Path.Combine(_directory, "empty-cache")).FullName;
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", emptyCache, "--out", report]));
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(report, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Run_Index_WriteFailure_ReportsError()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--synthetic", "2", "--out", _directory]));
        Assert.Contains("Error:", _output.ToString());
    }

    [TestMethod]
    public async Task Run_OutputIsBorrowedAndReportsAreRealFiles()
    {
        await using var output = new StringWriter();
        var runner = new BenchmarkRunner(output);
        var path = Path.Combine(_directory, "report with spaces.txt");
        Assert.AreEqual(0, await runner.RunAsync(["index", "--synthetic", "2", "--out", path]));
        var saved = await File.ReadAllTextAsync(path, TestContext.CancellationToken);
        Assert.Contains("--- exact-search ---", saved);
        Assert.Contains("--- substring-search ---", saved);
        Assert.Contains($"Report saved to {Path.GetFullPath(path)}", output.ToString());
        Assert.AreEqual(0, await runner.RunAsync(["compare", path, path, "exact-search"]));
        await output.WriteLineAsync("still owned by caller");
        Assert.Contains("still owned by caller", output.ToString());
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
        await File.WriteAllTextAsync(literalBaseline, "--- exact-search ---\n  Median: 4.000 ms; Matches: 1\n--- substring-search ---\n  Median: 4.000 ms; Matches: 1", TestContext.CancellationToken);
        await File.WriteAllTextAsync(grownPath, "--- exact-search ---\n  Median: 5.001 ms; Matches: 1\n--- substring-search ---\n  Median: 5.001 ms; Matches: 1", TestContext.CancellationToken);

        foreach (var scenario in new[] { "exact-search", "substring-search" })
        {
            Assert.AreEqual(1, await _runner.RunAsync(["compare", literalBaseline, grownPath, scenario]));
        }

        Assert.Contains("FAILED", _output.ToString());
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
        Assert.Contains("no median for scenario 'exact-search'", _output.ToString());
    }

    [TestMethod]
    public async Task Compare_MissingFiles_ReturnsOne()
    {
        var before = Path.Combine(_directory, "before.txt");
        var after = Path.Combine(_directory, "after.txt");
        Assert.AreEqual(1, await _runner.RunAsync(["compare", before, after, "exact-search"]));
        Assert.Contains($"Baseline before file not found: {before}", _output.ToString());
        await File.WriteAllTextAsync(before, "--- exact-search ---\n  Median: 1.000 ms", TestContext.CancellationToken);
        Assert.AreEqual(1, await _runner.RunAsync(["compare", before, after, "exact-search"]));
        Assert.Contains($"Benchmark after file not found: {after}", _output.ToString());
    }

    [TestMethod]
    public async Task Compare_MissingArguments_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["compare", "before.txt", "after.txt"]));
        Assert.Contains("Usage: Benchmark.exe compare", _output.ToString());
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
        var retired = RetiredScenarioRegex();
        var matches = Directory.EnumerateFiles(Path.Combine(root.FullName, "Benchmark"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path).Where(line => retired.IsMatch(line)).Select(line => $"{Path.GetFileName(path)}: {line.Trim()}"));
        Assert.AreSequenceEqual([], [.. matches]);
    }

    async Task<int> Compare(string beforeReport, string afterReport, string scenario = "exact-search")
    {
        var before = Path.Combine(_directory, "before.txt");
        var after = Path.Combine(_directory, "after.txt");
        await File.WriteAllTextAsync(before, beforeReport, TestContext.CancellationToken);
        await File.WriteAllTextAsync(after, afterReport, TestContext.CancellationToken);
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

    [GeneratedRegex(@"Bound: the (read \(I/O\)|parse \(CPU\)) stage dominates")]
    private static partial Regex MyRegex();

    [GeneratedRegex("compat|ToArray", RegexOptions.IgnoreCase)]
    private static partial Regex RetiredScenarioRegex();
}
