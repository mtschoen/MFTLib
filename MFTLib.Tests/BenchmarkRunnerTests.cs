using System.Globalization;
using System.Runtime.Versioning;
using Benchmark;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
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
        StringAssert.Contains(string.Join('\n', _consoleLines), "Usage: Benchmark.exe index");
    }

    [TestMethod]
    [DataRow(1, 3)]
    [DataRow(10, 4)]
    public async Task Run_Index_Synthetic_ReportsGeometryAndControlledMedians(int rows, int iterations)
    {
        var samples = new Queue<double>(iterations == 3
            ? [9, 1, 5, 8, 2, 4] : [9, 1, 5, 3, 8, 2, 4, 6]);
        _runner._getStopwatchElapsedMs = _ => samples.Dequeue();
        Assert.AreEqual(0, await _runner.RunAsync(["INDEX", "--synthetic", rows.ToString(CultureInfo.InvariantCulture), "--iterations", iterations.ToString(CultureInfo.InvariantCulture)]));
        var output = string.Join('\n', _consoleLines);
        StringAssert.Contains(output, $"Rows: {rows}");
        StringAssert.Contains(output, $"Slot capacity: {BlockLayout.ComputeSlotCapacity((uint)rows)}");
        StringAssert.Contains(output, "File bytes:");
        StringAssert.Contains(output, "Name-pool bytes:");
        StringAssert.Contains(output, "Name-pool capacity bytes:");
        StringAssert.Contains(output, "--- exact-search ---\n  Median: " + (iterations == 3 ? "5.000" : "4.000") + " ms");
        StringAssert.Contains(output, "--- substring-search ---\n  Median: " + (iterations == 3 ? "4.000" : "5.000") + " ms");
        StringAssert.Contains(output, $"Matches: {(rows == 1 ? 0 : 1)}");
        Assert.AreEqual(0, samples.Count);
    }

    [TestMethod]
    public async Task Run_Index_DefaultIterations_ReportsThreeSamples()
    {
        _runner._getStopwatchElapsedMs = _ => 7;
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        StringAssert.Contains(string.Join('\n', _consoleLines), "median of 3 runs");
    }

    [TestMethod]
    public async Task Run_Index_MeasurementFailure_ReportsError()
    {
        _runner._getStopwatchElapsedMs = _ => throw new IOException("measurement failed");
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--synthetic", "2"]));
        StringAssert.Contains(string.Join('\n', _consoleLines), "measurement failed");
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
            _runner._getStopwatchElapsedMs = _ => 12;
            Assert.AreEqual(0, await _runner.RunAsync(["index", "--cache-directory", directory]));
            var output = string.Join('\n', _consoleLines);
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
            StringAssert.Contains(string.Join('\n', _consoleLines), corrupt ? "Invalid" : "No cache blocks");
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
            StringAssert.Contains(string.Join('\n', _consoleLines), locked ? "InUse" : "Offline");
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
            _runner._getStopwatchElapsedMs = _ => 1;
            Assert.AreEqual(0, await _runner.RunAsync(["index", "--cache-directory", directory]),
                string.Join('\n', _consoleLines));
            StringAssert.Contains(string.Join('\n', _consoleLines), "Rows: 6");
            StringAssert.Contains(string.Join('\n', _consoleLines), "Name-pool bytes: 0 (0.00% of file)");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Run_NoVerb_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync([]));
        StringAssert.Contains(string.Join('\n', _consoleLines), "Usage: Benchmark.exe <index|compare>");
    }

    [TestMethod]
    public async Task Run_Index_PrintsSystemInfo()
    {
        _runner._getStopwatchElapsedMs = _ => 1;
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        var output = string.Join('\n', _consoleLines);
        StringAssert.Contains(output, "System Info");
        StringAssert.Contains(output, "CPU:     MockValue");
        StringAssert.Contains(output, "Disk:    MockDisk");
    }

    [TestMethod]
    public async Task Run_Index_WithoutOut_WritesNoFile()
    {
        _runner._getStopwatchElapsedMs = _ => 1;
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        Assert.AreEqual(0, _writtenFiles.Count);
    }

    [TestMethod]
    public async Task Run_Index_Failure_WritesNoReport()
    {
        _runner._getStopwatchElapsedMs = _ => throw new IOException("measurement failed");
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--synthetic", "2", "--out", "report.txt"]));
        Assert.AreEqual(0, _writtenFiles.Count);
    }

    [TestMethod]
    public async Task Compare_IndexReportAgainstItself_Passes_AndGrowthBeyondTheBoundFails()
    {
        _runner._getStopwatchElapsedMs = _ => 4;
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2", "--out", "baseline.txt"]));
        var baseline = _writtenFiles.Single().Content;
        _runner._fileExists = _ => true;
        _runner._readAllText = path => path == "grown.txt"
            ? baseline.Replace("Median: 4.000 ms", "Median: 5.001 ms", StringComparison.Ordinal)
            : baseline;

        foreach (var scenario in new[] { "exact-search", "substring-search" })
        {
            Assert.AreEqual(0, await _runner.RunAsync(["compare", "baseline.txt", "baseline.txt", scenario]));
            Assert.AreEqual(1, await _runner.RunAsync(["compare", "baseline.txt", "grown.txt", scenario]));
        }

        StringAssert.Contains(string.Join('\n', _consoleLines), "FAILED");
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
        StringAssert.Contains(string.Join('\n', _consoleLines), "no median for scenario 'exact-search'");
    }

    [TestMethod]
    public async Task Compare_MissingFiles_ReturnsOne()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["compare", "before.txt", "after.txt", "exact-search"]));
        StringAssert.Contains(string.Join('\n', _consoleLines), "Baseline before file not found: before.txt");
        _runner._fileExists = path => path == "before.txt";
        _runner._readAllText = _ => "--- exact-search ---\n  Median: 1.000 ms";
        Assert.AreEqual(1, await _runner.RunAsync(["compare", "before.txt", "after.txt", "exact-search"]));
        StringAssert.Contains(string.Join('\n', _consoleLines), "Benchmark after file not found: after.txt");
    }

    [TestMethod]
    public async Task Compare_MissingArguments_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["compare", "before.txt", "after.txt"]));
        StringAssert.Contains(string.Join('\n', _consoleLines), "Usage: Benchmark.exe compare");
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

    Task<int> Compare(string beforeReport, string afterReport, string scenario = "exact-search")
    {
        _runner._fileExists = _ => true;
        _runner._readAllText = path => path == "before.txt" ? beforeReport : afterReport;
        return _runner.RunAsync(["compare", "before.txt", "after.txt", scenario]);
    }

    List<string> _consoleLines = null!;
    BenchmarkRunner _runner = null!;
    List<(string Path, string Content)> _writtenFiles = null!;

    [TestInitialize]
    public void Initialize()
    {
        _consoleLines = [];
        _writtenFiles = [];
        _runner = new BenchmarkRunner
        {
            _systemInfo = new SystemInfo
            {
                _getBuildConfiguration = () => "Release",
                _getWmiValue = (_, _) => "MockValue",
                _getInstalledMemoryGb = () => 32,
                _getDiskModel = _ => "MockDisk"
            },
            _fileExists = _ => false,
            _writeAllText = (path, content) => _writtenFiles.Add((path, content)),
            _writeLineToConsole = line => _consoleLines.Add(line)
        };
    }
}
