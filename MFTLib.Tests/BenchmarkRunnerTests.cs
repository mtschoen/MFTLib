using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Benchmark;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // changes Environment.CurrentDirectory and PATH, which every concurrently running test class shares
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
        StringAssert.Contains(output, "Exact median: " + (iterations == 3 ? "5.000" : "4.000") + " ms");
        StringAssert.Contains(output, "Substring median: " + (iterations == 3 ? "4.000" : "5.000") + " ms");
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
                block.Header.UsnJournalId = 123;
                block.Header.UsnNextUsn = 456;
                writer.Complete(DateTime.UtcNow, null);
            }
            _runner._getStopwatchElapsedMs = _ => 1;
            Assert.AreEqual(0, await _runner.RunAsync(["index", "--cache-directory", directory]));
            StringAssert.Contains(string.Join('\n', _consoleLines), "Rows: 6");
            StringAssert.Contains(string.Join('\n', _consoleLines), "Name-pool bytes: 0 (0.00% of file)");
        }
        finally { Directory.Delete(directory, true); }
    }

    static readonly string[] EntryPointArgs = ["10", "1"];

    [TestMethod]
    public async Task RunAsync_ExistingScenario_PreservesParentBehavior()
    {
        Assert.AreEqual(0, await _runner.RunAsync(EntryPointArgs));
        StringAssert.Contains(string.Join('\n', _consoleLines), "--- Scenario: compat ---");
    }
    List<string> _consoleLines = null!;
    List<string> _consoleWrites = null!;
    List<string> _deletedFiles = null!;
    BenchmarkRunner _runner = null!;
    List<(string Path, string Content)> _writtenFiles = null!;

    [TestInitialize]
    public void Initialize()
    {
        _consoleLines = [];
        _consoleWrites = [];
        _deletedFiles = [];
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
            _getGitCommitHash = () => "9f17b3fd75215cef39788031ac1cc36dbbbed060",
            _getPeakWorkingSet64 = () => 500_000_000L,
            _getPeakPrivateBytes64 = () => 400_000_000L,
            _generateSynthetic = (_, _, _) => { },
            _deleteFile = path => _deletedFiles.Add(path),
            _getFileInfo = _ => new FileInfo(typeof(BenchmarkRunnerTests).Assembly.Location),
            _fileExists = _ => false,
            _readAllText = path => _writtenFiles.LastOrDefault(f => f.Path == path).Content ?? string.Empty,
            _writeAllText = (path, content) => _writtenFiles.Add((path, content)),
            _writeLineToConsole = line => _consoleLines.Add(line),
            _writeToConsole = value => _consoleWrites.Add(value),
            _runChildProcess = args =>
            {
                var scenario = args.Length > 1 ? args[1] : "compat";
                var stdout = $"""
                              --- Scenario: {scenario} ---
                                Results (median of 3 successful iterations):
                                  Records:              100,000
                                  Managed allocated:    5,000,000 bytes
                                  Peak working set:     500,000,000 bytes
                                  Peak private bytes:   {(scenario == "compat" ? 900_000_000L : 600_000_000L):N0} bytes
                                  Native compact bytes: 24,000,000 bytes
                                  Wall clock:           37.0ms
                                  Throughput:           2,700,000 records/sec (wall clock)
                              """;
                return (0, stdout, string.Empty);
            }
        };
    }

    [TestMethod]
    public void Run_DefaultArguments_Uses8MillionRecordsAnd3Iterations()
    {
        var childCalls = new List<string[]>();
        ulong generatedRecordCount = 0;
        _runner._generateSynthetic = (_, recordCount, _) => generatedRecordCount = recordCount;
        _runner._runChildProcess = args =>
        {
            childCalls.Add(args);
            return (0, $"--- Scenario: {args[1]} ---\n", "");
        };

        _runner.Run([]);

        Assert.AreEqual(3, childCalls.Count);
        Assert.AreEqual(8_000_000UL, generatedRecordCount);
        Assert.AreEqual("3", childCalls[0][3]);
    }

    [TestMethod]
    public void Run_CustomArguments_ParsesRecordCountAndIterations()
    {
        var childCalls = new List<string[]>();
        ulong generatedRecordCount = 0;
        _runner._generateSynthetic = (_, recordCount, _) => generatedRecordCount = recordCount;
        _runner._runChildProcess = args =>
        {
            childCalls.Add(args);
            return (0, $"--- Scenario: {args[1]} ---\n", "");
        };

        _runner.Run(["100000", "2"]);

        Assert.AreEqual(3, childCalls.Count);
        Assert.AreEqual(100_000UL, generatedRecordCount);
        Assert.AreEqual("2", childCalls[0][3]);
    }

    [TestMethod]
    public void Run_PrintsSystemInfoSection()
    {
        _runner.Run([]);

        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("System Info"));
        Assert.IsTrue(allOutput.Contains("Build:"));
        Assert.IsTrue(allOutput.Contains("MockValue")); // From mocked WMI
        Assert.IsTrue(allOutput.Contains("32 GB"));
        Assert.IsTrue(allOutput.Contains("MockDisk"));
    }

    [TestMethod]
    public void Run_RunsAllThreeScenarios()
    {
        _runner.Run([]);

        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("--- Scenario: compat ---"));
        Assert.IsTrue(allOutput.Contains("--- Scenario: bounded ---"));
        Assert.IsTrue(allOutput.Contains("--- Scenario: broker-stream ---"));
    }

    [TestMethod]
    public void Run_DeletesSyntheticFile()
    {
        _runner.Run([]);

        Assert.AreEqual(1, _deletedFiles.Count);
        Assert.IsTrue(_deletedFiles[0].EndsWith("synthetic.mft", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Run_WithOutOption_SavesReport()
    {
        _runner.Run(["--out", "report.txt"]);

        Assert.AreEqual(1, _writtenFiles.Count);
        Assert.IsTrue(_writtenFiles[0].Path.EndsWith("report.txt", StringComparison.Ordinal));
        Assert.IsTrue(_writtenFiles[0].Content.Contains("System Info"));
        Assert.IsTrue(_writtenFiles[0].Content.Contains("MFT Benchmark"));
    }

    [TestMethod]
    public void Run_WithoutOutOption_WritesNoFile()
    {
        _runner.Run([]);

        Assert.AreEqual(0, _writtenFiles.Count);
        Assert.IsTrue(_consoleLines.Any(line => line.Contains("Report not saved (pass --out <path> to write it).")));
    }

    [TestMethod]
    public void Run_WithoutCompareBaselineOption_DoesNotEvaluateThresholds()
    {
        var exitCode = _runner.Run([]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_consoleLines.Any(line =>
            line.Contains("Thresholds not evaluated (pass --compare-baseline <path> to enforce them).")));
        Assert.IsFalse(_consoleLines.Any(line => line.Contains("Threshold check:")));
    }

    [TestMethod]
    public void Run_WithCompareBaseline_MissingFile_ReturnsOne()
    {
        _runner._fileExists = _ => false;

        var exitCode = _runner.Run(["--compare-baseline", "missing-baseline.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(_consoleLines.Any(line => line.Contains("Error: Baseline before file not found:")));
    }

    [TestMethod]
    public void Run_WithCompareBaseline_EvaluatesAgainstMeasuredReport_NotAWrittenFile()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = _ => beforeContent;

        var exitCode = _runner.Run(["--compare-baseline", "before.txt"]);

        // No --out, so nothing was written; the thresholds still evaluate off the in-memory report.
        Assert.AreEqual(0, _writtenFiles.Count);
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_consoleLines.Any(line => line.Contains("Threshold check: PASSED")));
    }

    [TestMethod]
    public void Run_ReturnsZero()
    {
        Assert.AreEqual(0, _runner.Run([]));
    }

    [TestMethod]
    public void Run_PrintsRecordCountAndIterationsHeader()
    {
        _runner.Run(["500000", "2"]);

        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Records: 500,000"));
        Assert.IsTrue(allOutput.Contains("Iterations: 2"));
    }

    [TestMethod]
    public void Run_PrintsCleanupAndReportMessages()
    {
        _runner.Run(["--out", "report.txt"]);

        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Synthetic MFT file cleaned up."));
        Assert.IsTrue(allOutput.Contains("Report saved to"));
    }

    [TestMethod]
    public void Benchmark_EntryPoint_Executes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Exercises the real parent path end to end: generate, spawn the scenario children, report.
        // Writes only into a temp directory, and passes neither --out nor --compare-baseline against
        // tracked files, so a smoke-sized run is not measured against the full-size baseline.
        var reportPath = Path.Combine(Path.GetTempPath(), $"mftlib-benchmark-{Guid.NewGuid():N}.txt");
        var entryPoint = typeof(BenchmarkRunner).Assembly.EntryPoint!;
        try
        {
            var exitCode =
                entryPoint.Invoke(null, [new[] { EntryPointArgs[0], EntryPointArgs[1], "--out", reportPath }]);

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(File.Exists(reportPath));
            var report = File.ReadAllText(reportPath);
            Assert.IsTrue(report.Contains("Managed allocated:"));
            Assert.IsTrue(report.Contains("Peak private bytes:"));
            Assert.IsTrue(report.Contains("Native compact bytes:"));
            Assert.IsTrue(report.Contains("--- Scenario: compat ---"));
            Assert.IsTrue(report.Contains("--- Scenario: bounded ---"));
            Assert.IsTrue(report.Contains("--- Scenario: broker-stream ---"));
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    // --- measure subcommand tests ---

    [TestMethod]
    public void Run_Measure_Compat_RunsAndEmitsMemoryAndThroughputMetrics()
    {
        var allocCalls = 0;
        _runner._getTotalAllocatedBytes = () =>
        {
            allocCalls++;
            return allocCalls * 10_000_000L;
        };
        _runner._getPeakWorkingSet64 = () => 500_000_000L;
        _runner._getPeakPrivateBytes64 = () => 400_000_000L;
        _runner._getStopwatchElapsedMs = _ => 250.0;
        _runner._parseCompat = _ => (100_000, 20_000_000UL);
        _runner._fileExists = _ => true;

        var exitCode = _runner.Run(["measure", "compat", "fake.mft", "3"]);

        Assert.AreEqual(0, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("--- Scenario: compat ---"));
        Assert.IsTrue(allOutput.Contains("Managed allocated:"));
        Assert.IsTrue(allOutput.Contains("Peak working set:"));
        Assert.IsTrue(allOutput.Contains("Peak private bytes:"));
        Assert.IsTrue(allOutput.Contains("Native compact bytes:"));
        Assert.IsTrue(allOutput.Contains("Throughput:"));
        Assert.IsTrue(allOutput.Contains("20,000,000 bytes"));
    }

    [TestMethod]
    public void Run_Measure_Bounded_RunsAndEmitsMemoryAndThroughputMetrics()
    {
        var receivedBatchSize = 0;
        _runner._getTotalAllocatedBytes = () => 1_000_000L;
        _runner._getPeakWorkingSet64 = () => 300_000_000L;
        _runner._getPeakPrivateBytes64 = () => 200_000_000L;
        _runner._getStopwatchElapsedMs = _ => 200.0;
        _runner._parseBounded = (_, batchSize) =>
        {
            receivedBatchSize = batchSize;
            return (100_000, 20_000_000UL);
        };
        _runner._fileExists = _ => true;

        var exitCode = _runner.Run(["measure", "bounded", "fake.mft", "2"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(4096, receivedBatchSize);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("--- Scenario: bounded ---"));
        Assert.IsTrue(allOutput.Contains("Managed allocated:"));
        Assert.IsTrue(allOutput.Contains("Peak private bytes:"));
        Assert.IsTrue(allOutput.Contains("Native compact bytes:"));
    }

    [TestMethod]
    public void Run_Measure_BrokerStream_RunsAndEmitsMemoryAndThroughputMetrics()
    {
        var receivedBatchSize = 0;
        _runner._getTotalAllocatedBytes = () => 1_000_000L;
        _runner._getPeakWorkingSet64 = () => 350_000_000L;
        _runner._getPeakPrivateBytes64 = () => 250_000_000L;
        _runner._getStopwatchElapsedMs = _ => 220.0;
        _runner._parseBrokerStream = (_, batchSize) =>
        {
            receivedBatchSize = batchSize;
            return (100_000, 20_000_000UL);
        };
        _runner._fileExists = _ => true;

        var exitCode = _runner.Run(["measure", "broker-stream", "fake.mft", "2"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(4096, receivedBatchSize);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("--- Scenario: broker-stream ---"));
        Assert.IsTrue(allOutput.Contains("Managed allocated:"));
        Assert.IsTrue(allOutput.Contains("Peak private bytes:"));
        Assert.IsTrue(allOutput.Contains("Native compact bytes:"));
    }

    [TestMethod]
    public void Run_Measure_UnknownScenario_ReturnsOne()
    {
        _runner._fileExists = _ => true;
        var exitCode = _runner.Run(["measure", "unknown-scenario", "fake.mft", "1"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Unknown scenario"));
    }

    [TestMethod]
    public void Run_Measure_MissingArguments_ReturnsOne()
    {
        var exitCode = _runner.Run(["measure", "compat"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Usage:"));
    }

    [TestMethod]
    public void Run_Measure_NonExistentMftFile_ReturnsOne()
    {
        _runner._fileExists = _ => false;
        var exitCode = _runner.Run(["measure", "compat", "missing.mft", "1"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("MFT file not found"));
    }

    [TestMethod]
    public void Run_Measure_AllIterationsFail_ReturnsOne()
    {
        _runner._fileExists = _ => true;
        _runner._parseCompat = _ => throw new InvalidOperationException("read error");
        var exitCode = _runner.Run(["measure", "compat", "fake.mft", "2"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("All iterations failed"));
    }

    // --- compare subcommand tests ---

    [TestMethod]
    public void Run_Compare_ValidFiles_AllThresholdsPass_ReturnsZero_AndEmitsThresholds()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Benchmark arguments: 1000000 5
                                     Peak working set: 1546174464
                                     Peak private bytes: 1818877952

                                     --- Unfiltered (all records) ---
                                       Results (median of 5 successful iterations):
                                         Records:           749,599
                                         Wall clock:          374.4ms
                                         Throughput:      2,670,625 records/sec (wall clock)
                                     """;

        const string afterContent = """
                                    Git: d4b2f4d38e235e235e235e235e235e235e235e23
                                    Peak working set: 800000000
                                    Peak private bytes: 900000000

                                    --- Scenario: compat ---
                                      Results (median of 5 successful iterations):
                                        Records:              749,599
                                        Managed allocated:    50,000,000 bytes
                                        Peak working set:     800,000,000 bytes
                                        Peak private bytes:   900,000,000 bytes
                                        Native compact bytes: 24,000,000 bytes
                                        Wall clock:           370.0ms
                                        Throughput:           2,700,000 records/sec (wall clock)

                                    --- Scenario: bounded ---
                                      Results (median of 5 successful iterations):
                                        Records:              749,599
                                        Managed allocated:    5,000,000 bytes
                                        Peak working set:     500,000,000 bytes
                                        Peak private bytes:   600,000,000 bytes
                                        Native compact bytes: 24,000,000 bytes
                                        Wall clock:           380.0ms
                                        Throughput:           2,600,000 records/sec (wall clock)

                                    --- Scenario: broker-stream ---
                                      Results (median of 5 successful iterations):
                                        Records:              749,599
                                        Managed allocated:    8,000,000 bytes
                                        Peak working set:     550,000,000 bytes
                                        Peak private bytes:   650,000,000 bytes
                                        Native compact bytes: 24,000,000 bytes
                                        Wall clock:           390.0ms
                                        Throughput:           2,550,000 records/sec (wall clock)
                                    """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(0, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Comparison Report"));
        Assert.IsTrue(
            allOutput.Contains("Threshold: throughput regression <= 10.0%, peak private bytes reduction >= 40.0%"));
        Assert.IsTrue(allOutput.Contains("Threshold check: PASSED"));
    }

    [TestMethod]
    public void Run_Compare_BeforeBaselineWithComputeAndWallClockThroughput_ExtractsWallClock()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 9,246,511 records/sec (compute)
                                                 2,670,625 records/sec (wall clock)
                                     """;

        const string afterContent = """
                                    --- Scenario: compat ---
                                      Peak private bytes: 900,000,000 bytes
                                      Throughput: 2,680,000 records/sec (wall clock)
                                    --- Scenario: bounded ---
                                      Peak private bytes: 600,000,000 bytes
                                    --- Scenario: broker-stream ---
                                      Peak private bytes: 650,000,000 bytes
                                    """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(0, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Baseline throughput:") && allOutput.Contains("2,670,625 records/sec"));
        Assert.IsFalse(allOutput.Contains("9,246,511"));
        Assert.IsTrue(allOutput.Contains("Threshold check: PASSED"));
    }

    [DataTestMethod]
    [DataRow("2,000,000", "900,000,000", "600,000,000", "650,000,000", "Throughput regression")]
    [DataRow("2,700,000", "1,500,000,000", "600,000,000", "650,000,000", "Peak private bytes reduction")]
    [DataRow("2,700,000", "900,000,000", "1,000,000,000", "650,000,000", "Bounded <= Compat peak")]
    [DataRow("2,700,000", "900,000,000", "600,000,000", "1,000,000,000", "Broker-stream <= Compat peak")]
    public void Run_Compare_ThresholdFailure_ReturnsOneAndReportsFailingThreshold(
        string compatThroughput,
        string compatPrivateBytes,
        string boundedPrivateBytes,
        string brokerStreamPrivateBytes,
        string expectedFailingThreshold)
    {
        var exitCode = RunComparison(CreateScenarioReport(
            compatThroughput,
            compatPrivateBytes,
            boundedPrivateBytes,
            brokerStreamPrivateBytes));

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        var failingThresholdOutput = _consoleLines.Single(line => line.Contains(expectedFailingThreshold));
        Assert.IsTrue(failingThresholdOutput.Contains("FAIL"));
        Assert.IsTrue(allOutput.Contains("Threshold check: FAILED"));
    }

    [TestMethod]
    public void Run_Compare_MissingGitSha_ReturnsOne()
    {
        const string beforeContent = """
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = _ => beforeContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("missing 40-character hex Git SHA"));
    }

    [TestMethod]
    public void Run_Compare_MissingPeakPrivateBytes_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Throughput: 2,670,625 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = _ => beforeContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("missing or invalid Peak private bytes"));
    }

    [TestMethod]
    public void Run_Compare_MissingThroughput_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = _ => beforeContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("missing or invalid Throughput"));
    }

    [TestMethod]
    public void Run_Compare_NonExistentBeforeFile_ReturnsOne()
    {
        _runner._fileExists = path => !path.Contains("before");
        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Baseline before file not found"));
    }

    [TestMethod]
    public void Run_Compare_NonExistentAfterFile_ReturnsOne()
    {
        _runner._fileExists = path => !path.Contains("after");
        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Benchmark after file not found"));
    }

    [TestMethod]
    public void Run_Compare_MissingArguments_ReturnsOne()
    {
        var exitCode = _runner.Run(["compare"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Usage:"));
    }

    // --- Parent runner tests ---

    [TestMethod]
    public void Run_Parent_SpawnsIsolatedChildren_SavesReport_AndCompares()
    {
        var childCalls = new List<string[]>();
        _runner._getGitCommitHash = () => "d4b2f4d38e235e235e235e235e235e235e235e23";
        _runner._getPeakWorkingSet64 = () => 800_000_000L;
        _runner._getPeakPrivateBytes64 = () => 900_000_000L;
        _runner._runChildProcess = args =>
        {
            childCalls.Add(args);
            var scenario = args[1];
            var stdout = $"""
                          --- Scenario: {scenario} ---
                            Results (median of 2 successful iterations):
                              Records:              100,000
                              Managed allocated:    5,000,000 bytes
                              Peak working set:     500,000,000 bytes
                              Peak private bytes:   {(scenario == "compat" ? 900_000_000L : 600_000_000L):N0} bytes
                              Native compact bytes: 24,000,000 bytes
                              Wall clock:           37.0ms
                              Throughput:           2,700,000 records/sec (wall clock)
                          """;
            return (0, stdout, "");
        };

        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : _writtenFiles.Last().Content;

        var exitCode = _runner.Run(
            ["100000", "2", "--out", "report.txt", "--compare-baseline", "before.txt"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(3, childCalls.Count);
        Assert.AreEqual("compat", childCalls[0][1]);
        Assert.AreEqual("bounded", childCalls[1][1]);
        Assert.AreEqual("broker-stream", childCalls[2][1]);
        Assert.AreEqual(1, _writtenFiles.Count);
        Assert.IsTrue(_writtenFiles[0].Path.EndsWith("report.txt", StringComparison.Ordinal));
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(
            allOutput.Contains("Threshold: throughput regression <= 10.0%, peak private bytes reduction >= 40.0%"));
        Assert.IsTrue(allOutput.Contains("Threshold check: PASSED"));
    }

    [TestMethod]
    public void Run_Parent_ChildProcessFails_ReturnsOne()
    {
        _runner._getGitCommitHash = () => "d4b2f4d38e235e235e235e235e235e235e235e23";
        _runner._runChildProcess = args =>
        {
            if (args[1] == "bounded")
            {
                return (1, "", "Child crash");
            }

            return (0, $"--- Scenario: {args[1]} ---\nPeak private bytes: 100\nThroughput: 1000", "");
        };

        var exitCode = _runner.Run(["100000", "2"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(1, _deletedFiles.Count);
    }

    [TestMethod]
    public void DefaultGetGitCommitHash_ReturnsNonEmptySha()
    {
        var runner = new BenchmarkRunner();
        var sha = runner._getGitCommitHash();
        Assert.IsNotNull(sha);
        Assert.AreEqual(40, sha.Length);
    }

    [TestMethod]
    public void DefaultGetPeakPrivateBytes_KeepsAnAllocationThatWasAlreadyFreed()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // A reading taken while the block is committed is one the peak has already passed, so the
        // peak can never fall below it. The current private size, which is not a peak, drops by
        // the block's size the moment the block is freed.
        const int blockBytes = 64 * 1024 * 1024;
        var runner = new BenchmarkRunner();
        var block = Marshal.AllocHGlobal(blockBytes);
        long privateBytesWhileHeld;
        try
        {
            privateBytesWhileHeld = Process.GetCurrentProcess().PrivateMemorySize64;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }

        Assert.IsTrue(runner._getPeakPrivateBytes64() >= privateBytesWhileHeld,
            "the seam reported less than the process had committed earlier, so it is not a peak");
    }

    [TestMethod]
    public void Run_Compare_InvalidAfterMetrics_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;
        const string invalidAfter = "Invalid after content without metrics";

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : invalidAfter;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_FallbackTopLevelMetrics_MissingScenarios_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;
        const string fallbackAfter = """
                                     Git: d4b2f4d38e235e235e235e235e235e235e235e23
                                     Peak private bytes: 900,000,000
                                     Throughput: 2,700,000 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : fallbackAfter;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [DataTestMethod]
    [DataRow("missing-bounded")]
    [DataRow("missing-broker-stream")]
    [DataRow("missing-compat")]
    [DataRow("missing-compat-throughput")]
    [DataRow("invalid-compat-throughput")]
    [DataRow("invalid-compat-private-bytes")]
    [DataRow("invalid-bounded-private-bytes")]
    [DataRow("invalid-broker-stream-private-bytes")]
    public void Run_Compare_MissingOrMalformedScenarioMeasurements_ReturnsOne(string mutation)
    {
        var exitCode = RunComparison(CreateMalformedScenarioReport(mutation));

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Parent_CompareFails_ReturnsCompareExitCode()
    {
        _runner._getGitCommitHash = () => "d4b2f4d38e235e235e235e235e235e235e235e23";
        _runner._runChildProcess = args =>
        {
            var scenario = args[1];
            var stdout = $"""
                          --- Scenario: {scenario} ---
                            Results (median of 1 successful iteration):
                              Records:              100,000
                              Managed allocated:    5,000,000 bytes
                              Peak working set:     500,000,000 bytes
                              Peak private bytes:   1,500,000,000 bytes
                              Native compact bytes: 24,000,000 bytes
                              Wall clock:           37.0ms
                              Throughput:           2,700,000 records/sec (wall clock)
                          """;
            return (0, stdout, "");
        };

        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = _ => beforeContent;

        var exitCode = _runner.Run(["100000", "1", "--compare-baseline", "before.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("Threshold check: FAILED"));
    }

    [TestMethod]
    public void Run_Measure_ZeroElapsedWall_CalculatesZeroThroughput()
    {
        _runner._getStopwatchElapsedMs = _ => 0.0;
        _runner._parseCompat = _ => (1000, 50000UL);
        _runner._fileExists = _ => true;

        var exitCode = _runner.Run(["measure", "compat", "fake.mft", "1"]);

        Assert.AreEqual(0, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Throughput:") && allOutput.Contains("0 records/sec (wall clock)"));
    }

    // --- Default _getGitCommitHash fallback paths (BenchmarkRunner.cs) ---
    // These exercise the real default lambda directly (not the overridable seam), matching
    // the existing DefaultGetGitCommitHash_ReturnsNonEmptySha pattern of invoking live git.

    [TestMethod]
    public void DefaultGetGitCommitHash_NotInsideGitRepository_ReturnsFallbackSha()
    {
        var freshRunner = new BenchmarkRunner();
        var previousDirectory = Environment.CurrentDirectory;
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        try
        {
            // A drive root has no ancestor directory, so git's upward repository search
            // reliably fails here regardless of which machine this runs on.
            Environment.CurrentDirectory = driveRoot;
            var sha = freshRunner._getGitCommitHash();

            Assert.AreEqual("0000000000000000000000000000000000000000", sha);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [TestMethod]
    public void DefaultGetGitCommitHash_GitExecutableNotOnPath_ReturnsFallbackSha()
    {
        var freshRunner = new BenchmarkRunner();
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var sha = freshRunner._getGitCommitHash();

            Assert.AreEqual("0000000000000000000000000000000000000000", sha);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    // --- BenchmarkRunner.Measure.cs: iterations-argument default branch ---
    // Each of these leaves the trailing iterations argument unusable in a different way
    // (absent, non-numeric, non-positive), exercising a different short-circuit of the
    // "arguments.Length > 2 && int.TryParse(...) && parsedIterations > 0" condition while
    // landing on the same observable default of 3 iterations.

    [DataTestMethod]
    [DataRow()]
    [DataRow("not-a-number")]
    [DataRow("0")]
    public void Run_Measure_UnusableIterationsArgument_DefaultsToThreeIterations(string? iterationsArgument = null)
    {
        var callCount = 0;
        _runner._fileExists = _ => true;
        _runner._parseCompat = _ =>
        {
            callCount++;
            return (1000, 50000UL);
        };

        var arguments = iterationsArgument is null
            ? new[] { "measure", "compat", "fake.mft" }
            : new[] { "measure", "compat", "fake.mft", iterationsArgument };
        var exitCode = _runner.Run(arguments);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(3, callCount);
        Assert.IsTrue(_consoleWrites.Any(write => write.Contains("Iteration 3/3")));
    }

    // --- BenchmarkRunner.Measure.cs: ExecuteMeasureIteration switch discard arm ---
    // RunMeasure already rejects any scenario outside compat/bounded/broker-stream before
    // ExecuteMeasureIteration runs, so the discard arm's throw is unreachable through the
    // public Run(...) surface. ExecuteMeasureIteration is invoked directly via reflection to
    // exercise it; the surrounding try/catch in the method catches the throw itself and
    // reports it as a failed iteration, which is what this test observes.

    [TestMethod]
    public void ExecuteMeasureIteration_UnknownScenario_HitsSwitchDiscardArmAndLogsFailure()
    {
        var method = typeof(BenchmarkRunner).GetMethod("ExecuteMeasureIteration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var metrics = new ScenarioMetricAccumulator();

        method.Invoke(_runner, ["not-a-real-scenario", "fake.mft", 0, 1, metrics]);

        Assert.AreEqual(0, metrics.WallClocks.Count);
        var failureLine = _consoleLines.Single(line => line.Contains("FAILED:"));
        Assert.IsTrue(failureLine.Contains("Unknown scenario: not-a-real-scenario"));
    }

    int RunComparison(string afterContent)
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        return _runner.Run(["compare", "before.txt", "after.txt"]);
    }

    static string CreateScenarioReport(
        string compatThroughput = "2,700,000",
        string compatPrivateBytes = "900,000,000",
        string boundedPrivateBytes = "600,000,000",
        string brokerStreamPrivateBytes = "650,000,000") => $$"""
            --- Scenario: compat ---
              Peak private bytes: {{compatPrivateBytes}} bytes
              Throughput: {{compatThroughput}} records/sec
            --- Scenario: bounded ---
              Peak private bytes: {{boundedPrivateBytes}} bytes
            --- Scenario: broker-stream ---
              Peak private bytes: {{brokerStreamPrivateBytes}} bytes
            """;

    static string CreateMalformedScenarioReport(string mutation)
    {
        var report = CreateScenarioReport();
        return mutation switch
        {
            "missing-bounded" => report.Replace("""
                                                --- Scenario: bounded ---
                                                  Peak private bytes: 600,000,000 bytes
                                                """, string.Empty, StringComparison.Ordinal),
            "missing-broker-stream" => report.Replace("""
                                                      --- Scenario: broker-stream ---
                                                        Peak private bytes: 650,000,000 bytes
                                                      """, string.Empty, StringComparison.Ordinal),
            "missing-compat" => report.Replace("""
                                               --- Scenario: compat ---
                                                 Peak private bytes: 900,000,000 bytes
                                                 Throughput: 2,700,000 records/sec
                                               """, string.Empty, StringComparison.Ordinal),
            "missing-compat-throughput" => report.Replace(
                "  Throughput: 2,700,000 records/sec", string.Empty, StringComparison.Ordinal),
            "invalid-compat-throughput" => report.Replace(
                "2,700,000 records/sec", ",,, records/sec", StringComparison.Ordinal),
            "invalid-compat-private-bytes" => report.Replace(
                "900,000,000 bytes", ",,, bytes", StringComparison.Ordinal),
            "invalid-bounded-private-bytes" => report.Replace(
                "600,000,000 bytes", ",,, bytes", StringComparison.Ordinal),
            "invalid-broker-stream-private-bytes" => report.Replace(
                "650,000,000 bytes", ",,, bytes", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown report mutation.")
        };
    }
}
