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
            SystemInfo = new SystemInfo
            {
                GetBuildConfiguration = () => "Release",
                GetWmiValue = (_, _) => "MockValue",
                GetInstalledMemoryGB = () => 32,
                GetDiskModel = _ => "MockDisk"
            },
            GetGitCommitHash = () => "9f17b3fd75215cef39788031ac1cc36dbbbed060",
            GetPeakWorkingSet64 = () => 500_000_000L,
            GetPeakPrivateBytes64 = () => 400_000_000L,
            GenerateSynthetic = (_, _, _) => { },
            ParseFromFile = (_, _, _) => ([], default),
            DeleteFile = path => _deletedFiles.Add(path),
            GetFileInfo = _ => new FileInfo(typeof(BenchmarkRunnerTests).Assembly.Location),
            FileExists = _ => false,
            ReadAllText = path => _writtenFiles.LastOrDefault(f => f.Path == path).Content ?? string.Empty,
            WriteAllText = (path, content) => _writtenFiles.Add((path, content)),
            WriteLineToConsole = line => _consoleLines.Add(line),
            WriteToConsole = value => _consoleWrites.Add(value),
            RunChildProcess = args =>
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
        _runner.RunChildProcess = args =>
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
        _runner.RunChildProcess = args =>
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
        _runner.FileExists = _ => false;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = _ => beforeContent;

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
        _runner.ParseFromFile = (_, _, _) =>
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
        _runner.ParseFromFile = (_, _, _) => (new MftRecord[42], default);

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
            var (records, _) = freshRunner.ParseFromFile(temporaryPath, null, MatchFlags.None);

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
            var exitCode = entryPoint.Invoke(null, [new[] { EntryPointArgs[0], EntryPointArgs[1], "--out", reportPath }]);

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
                WriteAllText = (_, content) => File.WriteAllText(temporaryBaseline, content)
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
        _runner.ParseFromFile = (_, _, _) => throw new InvalidOperationException("boom");

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
        _runner.ParseFromFile = (_, _, _) =>
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
        _runner.GetTotalAllocatedBytes = () =>
        {
            allocCalls++;
            return allocCalls * 10_000_000L;
        };
        _runner.GetPeakWorkingSet64 = () => 500_000_000L;
        _runner.GetPeakPrivateBytes64 = () => 400_000_000L;
        _runner.GetStopwatchElapsedMs = _ => 250.0;
        _runner.ParseCompat = _ => (100_000, 20_000_000UL);
        _runner.FileExists = _ => true;

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
        _runner.GetTotalAllocatedBytes = () => 1_000_000L;
        _runner.GetPeakWorkingSet64 = () => 300_000_000L;
        _runner.GetPeakPrivateBytes64 = () => 200_000_000L;
        _runner.GetStopwatchElapsedMs = _ => 200.0;
        _runner.ParseBounded = (_, batchSize) =>
        {
            receivedBatchSize = batchSize;
            return (100_000, 20_000_000UL);
        };
        _runner.FileExists = _ => true;

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
        _runner.GetTotalAllocatedBytes = () => 1_000_000L;
        _runner.GetPeakWorkingSet64 = () => 350_000_000L;
        _runner.GetPeakPrivateBytes64 = () => 250_000_000L;
        _runner.GetStopwatchElapsedMs = _ => 220.0;
        _runner.ParseBrokerStream = (_, batchSize) =>
        {
            receivedBatchSize = batchSize;
            return (100_000, 20_000_000UL);
        };
        _runner.FileExists = _ => true;

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
        _runner.FileExists = _ => true;
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
        _runner.FileExists = _ => false;
        var exitCode = _runner.Run(["measure", "compat", "missing.mft", "1"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("MFT file not found"));
    }

    [TestMethod]
    public void Run_Measure_AllIterationsFail_ReturnsOne()
    {
        _runner.FileExists = _ => true;
        _runner.ParseCompat = _ => throw new InvalidOperationException("read error");
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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(0, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Comparison Report"));
        Assert.IsTrue(allOutput.Contains("Threshold: throughput regression <= 10.0%, peak private bytes reduction >= 40.0%"));
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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = _ => beforeContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = _ => beforeContent;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = _ => beforeContent;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("missing or invalid Throughput"));
    }

    [TestMethod]
    public void Run_Compare_NonExistentBeforeFile_ReturnsOne()
    {
        _runner.FileExists = path => !path.Contains("before");
        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Baseline before file not found"));
    }

    [TestMethod]
    public void Run_Compare_NonExistentAfterFile_ReturnsOne()
    {
        _runner.FileExists = path => !path.Contains("after");
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
        _runner.GetGitCommitHash = () => "d4b2f4d38e235e235e235e235e235e235e235e23";
        _runner.GetPeakWorkingSet64 = () => 800_000_000L;
        _runner.GetPeakPrivateBytes64 = () => 900_000_000L;
        _runner.RunChildProcess = args =>
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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : _writtenFiles.Last().Content;

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
        Assert.IsTrue(allOutput.Contains("Threshold: throughput regression <= 10.0%, peak private bytes reduction >= 40.0%"));
        Assert.IsTrue(allOutput.Contains("Threshold check: PASSED"));
    }

    [TestMethod]
    public void Run_Parent_ChildProcessFails_ReturnsOne()
    {
        _runner.GetGitCommitHash = () => "d4b2f4d38e235e235e235e235e235e235e235e23";
        _runner.RunChildProcess = args =>
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
        var sha = runner.GetGitCommitHash();
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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : invalidAfter;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : fallbackAfter;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterMissingBounded;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterMissingBroker;

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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = path => path.Contains("before") ? beforeContent : afterMissingCompat;

        var exitCode = _runner.Run(["compare", "before.txt", "after.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("missing required scenario measurements"));
    }

    [TestMethod]
    public void Run_Parent_CompareFails_ReturnsCompareExitCode()
    {
        _runner.GetGitCommitHash = () => "d4b2f4d38e235e235e235e235e235e235e235e23";
        _runner.RunChildProcess = args =>
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

        _runner.FileExists = _ => true;
        _runner.ReadAllText = _ => beforeContent;

        var exitCode = _runner.Run(["100000", "1", "--compare-baseline", "before.txt"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.Join("\n", _consoleLines).Contains("Threshold check: FAILED"));
    }

    [TestMethod]
    public void Run_Measure_ZeroElapsedWall_CalculatesZeroThroughput()
    {
        _runner.GetStopwatchElapsedMs = _ => 0.0;
        _runner.ParseCompat = _ => (1000, 50000UL);
        _runner.FileExists = _ => true;

        var exitCode = _runner.Run(["measure", "compat", "fake.mft", "1"]);

        Assert.AreEqual(0, exitCode);
        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Throughput:") && allOutput.Contains("0 records/sec (wall clock)"));
    }

}
