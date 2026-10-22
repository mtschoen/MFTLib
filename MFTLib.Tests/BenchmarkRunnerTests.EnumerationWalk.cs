using System.Collections.Concurrent;
using System.Globalization;
using Benchmark;
using MFTLib.Index;
using MFTLibTestExtensions;
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
        Assert.Contains("Usage: Benchmark.exe enumeration-walk", _output.ToString());
    }

    [TestMethod]
    [DataRow(1, 0, 3)]
    [DataRow(10, 2, 4)]
    public async Task Run_EnumerationWalk_ReportsBothModesAndDeletesGeneratedTree(int directories,
        int filesPerDirectory, int iterations)
    {
        var clock = new FakeTimeProvider();
        var visits = new ConcurrentDictionary<string, int>();
        EnumerationWalkLimit.Hooks._directoryEntered = (_, path, _) =>
        {
            visits.AddOrUpdate(path, 1, (_, count) => count + 1);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        };
        try
        {
            var reportPath = Path.Combine(_directory, "walk report.txt");
            await using var output = new StringWriter();
            var runner = new BenchmarkRunner(output, clock);
            Assert.AreEqual(0, await runner.RunAsync(["ENUMERATION-WALK",
                "--directories", directories.ToString(CultureInfo.InvariantCulture),
                "--files-per-directory", filesPerDirectory.ToString(CultureInfo.InvariantCulture),
                "--iterations", iterations.ToString(CultureInfo.InvariantCulture), "--out", reportPath]),
                output.ToString());

            var report = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken);
            Assert.StartsWith(report, output.ToString());
            Assert.Contains($"Directories: {directories} (including root)", report);
            Assert.Contains($"Files: {directories * filesPerDirectory}", report);
            Assert.Contains($"median of {iterations} runs", report);
            Assert.Contains("--- enumeration-walk-sequential ---", report);
            Assert.Contains("--- enumeration-walk-parallel ---", report);
            Assert.Contains(string.Create(CultureInfo.InvariantCulture,
                $"Median: {directories:F3} ms; Rows: {directories * (filesPerDirectory + 1)}"), report);
            Assert.Contains("Speedup (sequential / parallel): 1.000x", report);
            Assert.Contains("warm filesystem cache", report);
            Assert.HasCount(directories, visits);
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

        // Two warm-ups, then pairs that alternate their order. Every root starts a new walk;
        // producer drain guarantees its children finish before the next root is entered.
        bool[] sequential = [true, false, true, false, false, true];
        string? root = null;
        var walks = new List<WalkReads>();
        EnumerationWalkLimit.Hooks._directoryEntered = (_, path, _) =>
        {
            WalkReads current;
            lock (walks)
            {
                root ??= path;
                if (path == root)
                {
                    walks.Add(new WalkReads());
                }
                current = walks[^1];
            }

            var childOrder = current.Enter(child: path != root);
            // A parallel walk's first child read waits for a second, so overlap is observed, not raced.
            if (childOrder == 1 && !sequential[walks.Count - 1])
            {
                TestGate.WaitSynchronously(current.SecondChildEntered);
            }
        };
        EnumerationWalkLimit.Hooks._directoryExited = (_, _) =>
        {
            lock (walks)
            {
                walks[^1].Exit();
            }
        };
        try
        {
            Assert.AreEqual(0, await _runner.RunAsync(["enumeration-walk", "--directories", "9",
                "--files-per-directory", "0", "--iterations", "2"]), _output.ToString());
            Assert.HasCount(sequential.Length, walks);
            for (var walk = 0; walk < sequential.Length; walk++)
            {
                if (sequential[walk])
                {
                    Assert.AreEqual(1, walks[walk].Peak, "Sequential reads use only the base permit, one at a time.");
                }
                else
                {
                    Assert.IsGreaterThan(1, walks[walk].Peak, "Parallel reads use additional shared-budget readers.");
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
        await using var output = new StringWriter();
        var runner = new BenchmarkRunner(output, new FakeTimeProvider());
        Assert.AreEqual(0, await runner.RunAsync(["enumeration-walk", "--directories", "2"]));
        Assert.Contains("Files: 2", output.ToString());
        Assert.Contains("median of 5 runs", output.ToString());
        Assert.Contains("Speedup (sequential / parallel): unavailable", output.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_EnumerationWalk_IncompleteWalk_ReportsFailureAndCleansUp(bool throwBeforeReading)
    {
        var visits = new ConcurrentBag<string>();
        EnumerationWalkLimit.Hooks._directoryEntered = (_, path, _) =>
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
            await File.WriteAllTextAsync(report, "keep me", TestContext.CancellationToken);
            Assert.AreEqual(1, await _runner.RunAsync(["enumeration-walk", "--directories", "2", "--out", report]));
            Assert.Contains("Error:", _output.ToString());
            Assert.Contains(throwBeforeReading ? "injected read failure" : "Incomplete enumeration:", _output.ToString());
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(report, TestContext.CancellationToken));
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
        Assert.Contains("Error:", _output.ToString());
    }

    /// <summary>Concurrent directory reads observed for one walk.</summary>
    sealed class WalkReads
    {
        readonly TaskCompletionSource _secondChildEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _active;
        int _peak;
        int _children;

        internal Task SecondChildEntered => _secondChildEntered.Task;
        internal int Peak => Volatile.Read(ref _peak);

        /// <summary>Counts a read as active and returns its order among child reads, or zero for the root.</summary>
        internal int Enter(bool child)
        {
            var active = Interlocked.Increment(ref _active);
            var peak = Volatile.Read(ref _peak);
            while (active > peak && Interlocked.CompareExchange(ref _peak, active, peak) != peak)
            {
                peak = Volatile.Read(ref _peak);
            }

            if (!child)
            {
                return 0;
            }

            var order = Interlocked.Increment(ref _children);
            if (order == 2)
            {
                _secondChildEntered.TrySetResult();
            }

            return order;
        }

        internal void Exit() => Interlocked.Decrement(ref _active);
    }
}
