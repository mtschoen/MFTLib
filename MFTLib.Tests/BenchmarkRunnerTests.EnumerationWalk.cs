using System.Collections.Concurrent;
using System.Globalization;
using Benchmark;
using MFTLib.Index;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[DoNotParallelize]
public partial class BenchmarkRunnerTests
{
    [TestMethod]
    [DataRow("--directories", "0")]
    [DataRow("--directories", "invalid")]
    [DataRow("--directories", "2147483647")]
    [DataRow("--files-per-directory", "-1")]
    [DataRow("--iterations", "0")]
    [DataRow("--unknown", "1")]
    [DataRow("--directories")]
    [DataRow("--out")]
    [DataRow("--out", " ")]
    [DataRow("--directories", "2", "--directories", "3")]
    public async Task Run_EnumerationWalk_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["enumeration-walk", .. arguments]));
        StringAssert.Contains(_output.ToString(), "Usage: Benchmark.exe enumeration-walk");
    }

    [TestMethod]
    [DataRow(1, 0, 3)]
    [DataRow(10, 2, 4)]
    public async Task Run_EnumerationWalk_ReportsBothModesAndDeletesGeneratedTree(int directories,
        int filesPerDirectory, int iterations)
    {
        var clock = new FakeTimeProvider();
        var visits = new ConcurrentDictionary<string, int>();
        EnumerationWalkLimit.Hooks.DirectoryEntered = (_, path, _) =>
        {
            visits.AddOrUpdate(path, 1, (_, count) => count + 1);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        };
        try
        {
            var reportPath = Path.Combine(_directory, "walk report.txt");
            using var output = new StringWriter();
            var runner = new BenchmarkRunner(output, clock);
            Assert.AreEqual(0, await runner.RunAsync(["ENUMERATION-WALK",
                "--directories", directories.ToString(CultureInfo.InvariantCulture),
                "--files-per-directory", filesPerDirectory.ToString(CultureInfo.InvariantCulture),
                "--iterations", iterations.ToString(CultureInfo.InvariantCulture), "--out", reportPath]),
                output.ToString());

            var report = await File.ReadAllTextAsync(reportPath);
            StringAssert.StartsWith(output.ToString(), report);
            StringAssert.Contains(report, $"Directories: {directories} (including root)");
            StringAssert.Contains(report, $"Files: {directories * filesPerDirectory}");
            StringAssert.Contains(report, $"median of {iterations} runs");
            StringAssert.Contains(report, "--- enumeration-walk-sequential ---");
            StringAssert.Contains(report, "--- enumeration-walk-parallel ---");
            StringAssert.Contains(report, string.Create(CultureInfo.InvariantCulture,
                $"Median: {directories:F3} ms; Rows: {directories * (filesPerDirectory + 1)}"));
            StringAssert.Contains(report, "Speedup (sequential / parallel): 1.000x");
            StringAssert.Contains(report, "warm filesystem cache");
            Assert.AreEqual(directories, visits.Count);
            foreach (var (path, count) in visits)
            {
                Assert.AreEqual(2 * (iterations + 1), count, "Both modes warm up and repeat the whole walk.");
                Assert.IsFalse(Directory.Exists(path), "Generated directories are removed after measurement.");
            }
            foreach (var mode in new[] { "sequential", "parallel" })
            {
                Assert.AreEqual(0, await runner.RunAsync(["compare", reportPath, reportPath,
                    $"enumeration-walk-{mode}"]));
            }
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task Run_EnumerationWalk_UsesBaseOnlyThenDefaultSharedWorkers()
    {
        if (Environment.ProcessorCount < 2)
        {
            Assert.Inconclusive("Additional enumeration workers require at least two processors.");
        }

        string? root = null;
        var readerThreads = new List<HashSet<int>>();
        EnumerationWalkLimit.Hooks.DirectoryEntered = (_, path, _) =>
        {
            lock (readerThreads)
            {
                root ??= path;
                if (path == root)
                {
                    readerThreads.Add([]);
                }
                readerThreads[^1].Add(Environment.CurrentManagedThreadId);
            }
        };
        try
        {
            Assert.AreEqual(0, await _runner.RunAsync(["enumeration-walk", "--directories", "9",
                "--files-per-directory", "0", "--iterations", "2"]), _output.ToString());
            // Two warm-ups, then pairs that alternate their order. Every root starts a new walk;
            // producer drain guarantees its children finish before the next root is entered.
            bool[] sequential = [true, false, true, false, false, true];
            Assert.AreEqual(sequential.Length, readerThreads.Count);
            for (var walk = 0; walk < sequential.Length; walk++)
            {
                if (sequential[walk])
                {
                    Assert.HasCount(1, readerThreads[walk], "Sequential reads use only the base lane.");
                }
                else
                {
                    Assert.IsTrue(readerThreads[walk].Count > 1, "Parallel reads use additional shared-budget workers.");
                }
            }
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task Run_EnumerationWalk_DefaultsAndZeroClock_ReportUnavailableRatio()
    {
        using var output = new StringWriter();
        var runner = new BenchmarkRunner(output, new FakeTimeProvider());
        Assert.AreEqual(0, await runner.RunAsync(["enumeration-walk", "--directories", "2"]));
        StringAssert.Contains(output.ToString(), "Files: 2");
        StringAssert.Contains(output.ToString(), "median of 5 runs");
        StringAssert.Contains(output.ToString(), "Speedup (sequential / parallel): unavailable");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_EnumerationWalk_IncompleteWalk_ReportsFailureAndCleansUp(bool throwBeforeReading)
    {
        var visits = new ConcurrentBag<string>();
        EnumerationWalkLimit.Hooks.DirectoryEntered = (_, path, _) =>
        {
            visits.Add(path);
            if (throwBeforeReading)
            {
                throw new IOException("injected read failure");
            }
            Directory.Delete(path, recursive: true);
        };
        try
        {
            var report = Path.Combine(_directory, "report.txt");
            await File.WriteAllTextAsync(report, "keep me");
            Assert.AreEqual(1, await _runner.RunAsync(["enumeration-walk", "--directories", "2", "--out", report]));
            StringAssert.Contains(_output.ToString(), "Error:");
            StringAssert.Contains(_output.ToString(), throwBeforeReading ? "injected read failure" : "Incomplete enumeration:");
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(report));
            Assert.IsNotEmpty(visits);
            foreach (var path in visits)
            {
                Assert.IsFalse(Directory.Exists(path));
            }
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task Run_EnumerationWalk_WriteFailure_ReportsError()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["enumeration-walk", "--directories", "1", "--out", _directory]));
        StringAssert.Contains(_output.ToString(), "Error:");
    }
}
