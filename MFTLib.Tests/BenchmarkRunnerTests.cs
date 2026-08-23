using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Benchmark;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BenchmarkRunnerTests
{
    static readonly string[] EntryPointArgs = ["10", "1"];
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
            _parseFromFile = (_, _, _) => ([], default),
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
        _runner._runChildProcess = args =>
        {
            childCalls.Add(args);
            return (0, $"--- Scenario: {args[1]} ---\n", "");
        };

        _runner.Run([]);

        Assert.AreEqual(3, childCalls.Count);
        Assert.AreEqual("3", childCalls[0][3]);
    }

    [TestMethod]
    public void Run_CustomArguments_ParsesRecordCountAndIterations()
    {
        var childCalls = new List<string[]>();
        _runner._runChildProcess = args =>
        {
            childCalls.Add(args);
            return (0, $"--- Scenario: {args[1]} ---\n", "");
        };

        _runner.Run(["100000", "2"]);

        Assert.AreEqual(3, childCalls.Count);
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
    public void RunScenario_WithMultipleIterations_ComputesMedians()
    {
        var callCount = 0;
        _runner._parseFromFile = (_, _, _) =>
        {
            callCount++;
            return (new MftRecord[callCount * 10], default);
        };

        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario(new BenchmarkScenario("Test Scenario", null, MatchFlags.None), "fake.mft", 3, 100,
            logLines.Add, output);

        Assert.AreEqual(3, callCount);
        Assert.IsTrue(logLines.Any(line => line.Contains("Test Scenario")));
        Assert.IsTrue(logLines.Any(line => line.Contains("Results (median")));
        Assert.IsTrue(logLines.Any(line => line.Contains("Wall clock:")));
        Assert.IsTrue(logLines.Any(line => line.Contains("Throughput:")));
    }

    [TestMethod]
    public void RunScenario_SingleIteration_Works()
    {
        _runner._parseFromFile = (_, _, _) => (new MftRecord[42], default);

        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario(new BenchmarkScenario("Single", null, MatchFlags.None), "fake.mft", 1, 1000, logLines.Add,
            output);

        Assert.IsTrue(logLines.Any(line => line.Contains("42")));
    }

    [TestMethod]
    public void DefaultParseFromFile_CallsNative()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var temporaryPath = Path.GetTempFileName();
        try
        {
            File.Delete(temporaryPath);
            MftVolume.GenerateSyntheticMFT(temporaryPath, 10, 256);

            var freshRunner = new BenchmarkRunner();
            var (records, _) = freshRunner._parseFromFile(temporaryPath, null, MatchFlags.None);

            Assert.IsNotNull(records);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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

    [TestMethod]
    public void Benchmark_EndToEnd_WithNativeCalls_RunsAndExits()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var temporaryBaseline = Path.GetTempFileName();
        try
        {
            var runner = new BenchmarkRunner
            {
                _writeAllText = (_, content) => File.WriteAllText(temporaryBaseline, content)
            };
            var exitCode = runner.Run(["10", "1", "--out", temporaryBaseline]);
            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(File.Exists(temporaryBaseline));
            var content = File.ReadAllText(temporaryBaseline);
            Assert.IsTrue(content.Contains("MFT Benchmark"));
        }
        finally
        {
            if (File.Exists(temporaryBaseline))
            {
                File.Delete(temporaryBaseline);
            }
        }
    }

    [TestMethod]
    public void RunScenario_OutputIncludesAllTimingFields()
    {
        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario(new BenchmarkScenario("Format Test", "test", MatchFlags.ExactMatch), "fake.mft", 1, 5000,
            logLines.Add, output);

        var allOutput = string.Join("\n", logLines);
        Assert.IsTrue(allOutput.Contains("I/O:"));
        Assert.IsTrue(allOutput.Contains("Fixup:"));
        Assert.IsTrue(allOutput.Contains("Parse:"));
        Assert.IsTrue(allOutput.Contains("Marshal:"));
        Assert.IsTrue(allOutput.Contains("Compute:"));
    }

    [TestMethod]
    public void RunScenario_AllIterationsFail_PrintsNoResults()
    {
        _runner._parseFromFile = (_, _, _) => throw new InvalidOperationException("boom");

        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario(new BenchmarkScenario("Failing", null, MatchFlags.None), "fake.mft", 3, 100, logLines.Add,
            output);

        var allOutput = string.Join("\n", logLines);
        Assert.IsTrue(allOutput.Contains("All iterations failed"));
        Assert.IsFalse(allOutput.Contains("Throughput:"));
    }

    [TestMethod]
    public void RunScenario_PartialFailure_ReportsSuccessfulIterations()
    {
        var callCount = 0;
        _runner._parseFromFile = (_, _, _) =>
        {
            callCount++;
            if (callCount == 2)
            {
                throw new InvalidOperationException("boom");
            }

            return (new MftRecord[10], default);
        };

        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario(new BenchmarkScenario("Partial", null, MatchFlags.None), "fake.mft", 3, 100, logLines.Add,
            output);

        var outputText = output.ToString();
        Assert.IsTrue(outputText.Contains("FAILED:"));
        var allLogOutput = string.Join("\n", logLines);
        Assert.IsTrue(allLogOutput.Contains("Results (median of 2 successful iteration"));
        Assert.IsTrue(allLogOutput.Contains("Throughput:"));
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

    [TestMethod]
    public void Run_Compare_ThroughputRegressionExceeds10Percent_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        const string afterContent = """
                                    --- Scenario: compat ---
                                      Peak private bytes: 900,000,000 bytes
                                      Throughput: 2,000,000 records/sec
                                    --- Scenario: bounded ---
                                      Peak private bytes: 600,000,000 bytes
                                    --- Scenario: broker-stream ---
                                      Peak private bytes: 650,000,000 bytes
                                    """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Threshold check: FAILED"));
    }

    [TestMethod]
    public void Run_Compare_PeakPrivateBytesReductionBelow40Percent_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        const string afterContent = """
                                    --- Scenario: compat ---
                                      Peak private bytes: 1,500,000,000 bytes
                                      Throughput: 2,700,000 records/sec
                                    --- Scenario: bounded ---
                                      Peak private bytes: 600,000,000 bytes
                                    --- Scenario: broker-stream ---
                                      Peak private bytes: 650,000,000 bytes
                                    """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Threshold check: FAILED"));
    }

    [TestMethod]
    public void Run_Compare_BoundedPeakExceedsCompatPeak_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        const string afterContent = """
                                    --- Scenario: compat ---
                                      Peak private bytes: 900,000,000 bytes
                                      Throughput: 2,700,000 records/sec
                                    --- Scenario: bounded ---
                                      Peak private bytes: 1,000,000,000 bytes
                                    --- Scenario: broker-stream ---
                                      Peak private bytes: 650,000,000 bytes
                                    """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Threshold check: FAILED"));
    }

    [TestMethod]
    public void Run_Compare_BrokerStreamPeakExceedsCompatPeak_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;

        const string afterContent = """
                                    --- Scenario: compat ---
                                      Peak private bytes: 900,000,000 bytes
                                      Throughput: 2,700,000 records/sec
                                    --- Scenario: bounded ---
                                      Peak private bytes: 600,000,000 bytes
                                    --- Scenario: broker-stream ---
                                      Peak private bytes: 1,000,000,000 bytes
                                    """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
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

    [TestMethod]
    public void Run_Compare_MissingBoundedScenario_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;
        const string afterMissingBounded = """
                                           --- Scenario: compat ---
                                             Peak private bytes: 900,000,000 bytes
                                             Throughput: 2,700,000 records/sec
                                           --- Scenario: broker-stream ---
                                             Peak private bytes: 650,000,000 bytes
                                           """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterMissingBounded;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_MissingBrokerStreamScenario_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;
        const string afterMissingBroker = """
                                          --- Scenario: compat ---
                                            Peak private bytes: 900,000,000 bytes
                                            Throughput: 2,700,000 records/sec
                                          --- Scenario: bounded ---
                                            Peak private bytes: 600,000,000 bytes
                                          """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterMissingBroker;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_MissingCompatScenario_Fails_ReturnsOne()
    {
        const string beforeContent = """
                                     Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
                                     Peak private bytes: 1818877952
                                     Throughput: 2,670,625 records/sec
                                     """;
        const string afterMissingCompat = """
                                          --- Scenario: bounded ---
                                            Peak private bytes: 600,000,000 bytes
                                          --- Scenario: broker-stream ---
                                            Peak private bytes: 650,000,000 bytes
                                          """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterMissingCompat;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

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

    // --- BenchmarkRunner.Compare.cs: EvaluateThresholds guard-clause branches ---
    // ValidateBeforeBaseline always rejects a non-positive throughput or peak-private-bytes
    // value before EvaluateThresholds runs, so the "not positive" side of these ternaries is
    // unreachable through the public Run(...) surface. EvaluateThresholds is called directly
    // via reflection (the same private-member pattern already used elsewhere in this suite,
    // e.g. JournalBrokerClientTests) to exercise it.

    [TestMethod]
    public void EvaluateThresholds_BeforeThroughputNotPositive_SkipsRegressionFormula()
    {
        var method = typeof(BenchmarkRunner).GetMethod("EvaluateThresholds", BindingFlags.NonPublic | BindingFlags.Static)!;
        var logLines = new List<string>();
        var metrics = new ReportMetrics(2_500_000, 900_000_000, 600_000_000, 650_000_000);

        method.Invoke(null,
        [
            "9f17b3fd75215cef39788031ac1cc36dbbbed060", 900_000_000L, 0.0, metrics, (Action<string>)logLines.Add
        ]);

        var allOutput = string.Join("\n", logLines);
        Assert.IsTrue(allOutput.Contains("Throughput regression:") && allOutput.Contains("0.0%"));
        Assert.IsFalse(allOutput.Contains("Infinity"));
    }

    [TestMethod]
    public void EvaluateThresholds_BeforePeakPrivateBytesNotPositive_SkipsReductionFormula()
    {
        var method = typeof(BenchmarkRunner).GetMethod("EvaluateThresholds", BindingFlags.NonPublic | BindingFlags.Static)!;
        var logLines = new List<string>();
        var metrics = new ReportMetrics(2_500_000, 900_000_000, 600_000_000, 650_000_000);

        var result = (int)method.Invoke(null,
        [
            "9f17b3fd75215cef39788031ac1cc36dbbbed060", 0L, 2_500_000.0, metrics, (Action<string>)logLines.Add
        ])!;

        Assert.AreEqual(1, result);
        var allOutput = string.Join("\n", logLines);
        Assert.IsTrue(allOutput.Contains("Peak private bytes reduction:") && allOutput.Contains("0.0%"));
        Assert.IsFalse(allOutput.Contains("Infinity"));
    }

    // --- BenchmarkRunner.Compare.cs: ExtractReportMetrics regex/parse failure branches ---
    // Each case below leaves one scenario block matching but a single measurement either
    // absent (regex Success = false) or present as a comma-only capture (regex Success = true,
    // but the numeric parse fails), isolating one guard clause per test.

    [TestMethod]
    public void Run_Compare_AfterCompatBlockMissingThroughputPattern_ReturnsOne()
    {
        const string beforeContent = """
        Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
        Peak private bytes: 1818877952
        Throughput: 2,670,625 records/sec
        """;
        const string afterContent = """
        --- Scenario: compat ---
          Peak private bytes: 900,000,000 bytes
        --- Scenario: bounded ---
          Peak private bytes: 600,000,000 bytes
        --- Scenario: broker-stream ---
          Peak private bytes: 650,000,000 bytes
        """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_AfterCompatThroughputCommaOnly_FailsNumericParse_ReturnsOne()
    {
        const string beforeContent = """
        Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
        Peak private bytes: 1818877952
        Throughput: 2,670,625 records/sec
        """;
        const string afterContent = """
        --- Scenario: compat ---
          Peak private bytes: 900,000,000 bytes
          Throughput: ,,, records/sec
        --- Scenario: bounded ---
          Peak private bytes: 600,000,000 bytes
        --- Scenario: broker-stream ---
          Peak private bytes: 650,000,000 bytes
        """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_AfterCompatPeakPrivateBytesCommaOnly_FailsNumericParse_ReturnsOne()
    {
        const string beforeContent = """
        Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
        Peak private bytes: 1818877952
        Throughput: 2,670,625 records/sec
        """;
        const string afterContent = """
        --- Scenario: compat ---
          Peak private bytes: ,,, bytes
          Throughput: 2,700,000 records/sec
        --- Scenario: bounded ---
          Peak private bytes: 600,000,000 bytes
        --- Scenario: broker-stream ---
          Peak private bytes: 650,000,000 bytes
        """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_AfterBoundedPeakPrivateBytesCommaOnly_FailsNumericParse_ReturnsOne()
    {
        const string beforeContent = """
        Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
        Peak private bytes: 1818877952
        Throughput: 2,670,625 records/sec
        """;
        const string afterContent = """
        --- Scenario: compat ---
          Peak private bytes: 900,000,000 bytes
          Throughput: 2,700,000 records/sec
        --- Scenario: bounded ---
          Peak private bytes: ,,, bytes
        --- Scenario: broker-stream ---
          Peak private bytes: 650,000,000 bytes
        """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Compare_AfterBrokerStreamPeakPrivateBytesCommaOnly_FailsNumericParse_ReturnsOne()
    {
        const string beforeContent = """
        Git: 9f17b3fd75215cef39788031ac1cc36dbbbed060
        Peak private bytes: 1818877952
        Throughput: 2,670,625 records/sec
        """;
        const string afterContent = """
        --- Scenario: compat ---
          Peak private bytes: 900,000,000 bytes
          Throughput: 2,700,000 records/sec
        --- Scenario: bounded ---
          Peak private bytes: 600,000,000 bytes
        --- Scenario: broker-stream ---
          Peak private bytes: ,,, bytes
        """;

        _runner._fileExists = _ => true;
        _runner._readAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    // --- BenchmarkRunner.Measure.cs: iterations-argument default branch ---
    // Each of these leaves the trailing iterations argument unusable in a different way
    // (absent, non-numeric, non-positive), exercising a different short-circuit of the
    // "arguments.Length > 2 && int.TryParse(...) && parsedIterations > 0" condition while
    // landing on the same observable default of 3 iterations.

    [TestMethod]
    public void Run_Measure_NoIterationsArgument_DefaultsToThreeIterations()
    {
        var callCount = 0;
        _runner._fileExists = _ => true;
        _runner._parseCompat = _ =>
        {
            callCount++;
            return (1000, 50000UL);
        };

        var exitCode = _runner.Run(["measure", "compat", "fake.mft"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(3, callCount);
        Assert.IsTrue(_consoleWrites.Any(write => write.Contains("Iteration 3/3")));
    }

    [TestMethod]
    public void Run_Measure_NonNumericIterationsArgument_DefaultsToThreeIterations()
    {
        var callCount = 0;
        _runner._fileExists = _ => true;
        _runner._parseCompat = _ =>
        {
            callCount++;
            return (1000, 50000UL);
        };

        var exitCode = _runner.Run(["measure", "compat", "fake.mft", "not-a-number"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(3, callCount);
        Assert.IsTrue(_consoleWrites.Any(write => write.Contains("Iteration 3/3")));
    }

    [TestMethod]
    public void Run_Measure_ZeroIterationsArgument_DefaultsToThreeIterations()
    {
        var callCount = 0;
        _runner._fileExists = _ => true;
        _runner._parseCompat = _ =>
        {
            callCount++;
            return (1000, 50000UL);
        };

        var exitCode = _runner.Run(["measure", "compat", "fake.mft", "0"]);

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
        var output = new StringBuilder();
        var metrics = new ScenarioMetricAccumulator();

        method.Invoke(_runner, ["not-a-real-scenario", "fake.mft", 0, 1, output, metrics]);

        Assert.AreEqual(0, metrics.WallClocks.Count);
        var outputText = output.ToString();
        Assert.IsTrue(outputText.Contains("FAILED:"));
        Assert.IsTrue(outputText.Contains("Unknown scenario: not-a-real-scenario"));
    }
}
