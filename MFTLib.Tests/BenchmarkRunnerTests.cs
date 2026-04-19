using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Benchmark;

namespace MFTLib.Tests;

[TestClass]
public class BenchmarkRunnerTests
{
    BenchmarkRunner _runner = null!;
    List<string> _consoleLines = null!;
    List<string> _consoleWrites = null!;
    List<string> _deletedFiles = null!;
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
                GetDiskModel = _ => "MockDisk",
            },
            GenerateSynthetic = (_, _, _) => { },
            ParseFromFile = (_, _, _) => ([], default),
            DeleteFile = path => _deletedFiles.Add(path),
            GetFileInfo = _ => new FileInfo(typeof(BenchmarkRunnerTests).Assembly.Location),
            WriteAllText = (path, content) => _writtenFiles.Add((path, content)),
            WriteLineToConsole = line => _consoleLines.Add(line),
            WriteToConsole = value => _consoleWrites.Add(value),
        };
    }

    [TestMethod]
    public void Run_DefaultArguments_Uses8MillionRecordsAnd3Iterations()
    {
        _runner.Run([]);

        var parseCalls = _consoleWrites.Count(write => write.StartsWith("  Iteration "));
        Assert.AreEqual(12, parseCalls); // 4 scenarios * 3 iterations
    }

    [TestMethod]
    public void Run_CustomArguments_ParsesRecordCountAndIterations()
    {
        _runner.Run(["100000", "2"]);

        var parseCalls = _consoleWrites.Count(write => write.StartsWith("  Iteration "));
        Assert.AreEqual(8, parseCalls); // 4 scenarios * 2 iterations
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
    public void Run_RunsAllFourScenarios()
    {
        _runner.Run([]);

        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Unfiltered (all records)"));
        Assert.IsTrue(allOutput.Contains("Filtered: exact \".git\""));
        Assert.IsTrue(allOutput.Contains("Filtered: contains \"config\""));
        Assert.IsTrue(allOutput.Contains("Filtered: exact \".git\" + paths"));
    }

    [TestMethod]
    public void Run_DeletesSyntheticFile()
    {
        _runner.Run([]);

        Assert.AreEqual(1, _deletedFiles.Count);
        Assert.IsTrue(_deletedFiles[0].EndsWith("synthetic.mft"));
    }

    [TestMethod]
    public void Run_SavesBaseline()
    {
        _runner.Run([]);

        Assert.AreEqual(1, _writtenFiles.Count);
        Assert.IsTrue(_writtenFiles[0].Path.EndsWith("baseline.txt"));
        Assert.IsTrue(_writtenFiles[0].Content.Contains("System Info"));
        Assert.IsTrue(_writtenFiles[0].Content.Contains("MFT Benchmark"));
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
    public void Run_PrintsCleanupAndBaselineMessages()
    {
        _runner.Run([]);

        var allOutput = string.Join("\n", _consoleLines);
        Assert.IsTrue(allOutput.Contains("Synthetic MFT file cleaned up."));
        Assert.IsTrue(allOutput.Contains("Baseline saved to"));
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
        _runner.RunScenario("Test Scenario", null, MatchFlags.None, "fake.mft", 3, 100, logLines.Add, output);

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
        _runner.RunScenario("Single", null, MatchFlags.None, "fake.mft", 1, 1000, logLines.Add, output);

        Assert.IsTrue(logLines.Any(line => line.Contains("42")));
    }

    [TestMethod]
    public void DefaultParseFromFile_CallsNative()
    {
        var temporaryPath = Path.GetTempFileName();
        try
        {
            File.Delete(temporaryPath);
            MftVolume.GenerateSyntheticMFT(temporaryPath, 10, 256);

            var freshRunner = new BenchmarkRunner();
            var (records, timings) = freshRunner.ParseFromFile(temporaryPath, null, MatchFlags.None);

            Assert.IsNotNull(records);
            Assert.IsTrue(records.Length >= 0);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    [TestMethod]
    public void Benchmark_EntryPoint_Executes()
    {
        var entryPoint = typeof(BenchmarkRunner).Assembly.EntryPoint!;
        var baselinePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Benchmark", "baseline.txt"));
        var backup = File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : null;
        try
        {
            var exitCode = entryPoint.Invoke(null, [new[] { "10", "1" }]);
            Assert.AreEqual(0, exitCode);
        }
        finally
        {
            if (backup != null)
                File.WriteAllText(baselinePath, backup);
        }
    }

    [TestMethod]
    public void Benchmark_EndToEnd_WithNativeCalls_RunsAndExits()
    {
        var temporaryBaseline = Path.GetTempFileName();
        try
        {
            var runner = new BenchmarkRunner
            {
                WriteAllText = (_, content) => File.WriteAllText(temporaryBaseline, content),
            };
            var exitCode = runner.Run(["10", "1"]);
            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(File.Exists(temporaryBaseline));
            var content = File.ReadAllText(temporaryBaseline);
            Assert.IsTrue(content.Contains("MFT Benchmark"));
        }
        finally
        {
            if (File.Exists(temporaryBaseline))
                File.Delete(temporaryBaseline);
        }
    }

    [TestMethod]
    public void RunScenario_OutputIncludesAllTimingFields()
    {
        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario("Format Test", "test", MatchFlags.ExactMatch, "fake.mft", 1, 5000, logLines.Add, output);

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
        _runner.RunScenario("Failing", null, MatchFlags.None, "fake.mft", 3, 100, logLines.Add, output);

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
            if (callCount == 2) throw new InvalidOperationException("boom");
            return (new MftRecord[10], default);
        };

        var logLines = new List<string>();
        var output = new StringBuilder();
        _runner.RunScenario("Partial", null, MatchFlags.None, "fake.mft", 3, 100, logLines.Add, output);

        var outputText = output.ToString();
        Assert.IsTrue(outputText.Contains("FAILED:"));
        var allLogOutput = string.Join("\n", logLines);
        Assert.IsTrue(allLogOutput.Contains("Results (median of 2 successful iteration"));
        Assert.IsTrue(allLogOutput.Contains("Throughput:"));
    }
}
