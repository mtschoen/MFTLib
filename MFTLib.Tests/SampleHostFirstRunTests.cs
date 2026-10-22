using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;
using DirectHost = SampleProgram.Direct.SampleHost;

namespace MFTLib.Tests;

// What a double-clicked sample does with no arguments: the Direct usage and the window hold shared by both samples,
// and the output.log notice of an elevated relaunch. The consoles are seams; nothing here touches a real window.
[TestClass]
[DoNotParallelize]
public class SampleHostFirstRunTests
{
    string? _previousUnattended;

    [TestInitialize]
    public void SetUp()
    {
        _previousUnattended = Environment.GetEnvironmentVariable(DirectHost.UnattendedVariableName);
        Environment.SetEnvironmentVariable(DirectHost.UnattendedVariableName, null);
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(DirectHost.UnattendedVariableName, _previousUnattended);
    }

    [TestMethod]
    public void Run_OutputIsBorrowedAndStreamsControlPausing()
    {
        using var output = new StringWriter();
        using var input = new StringReader("xy");
        var directory = Directory.CreateTempSubdirectory("sample-stream-").FullName;
        try
        {
            var host = new DirectHost(output, input, directory);
            host.PauseBeforeExit(ownsWindow: true);
            Assert.Contains(DirectHost.PausePrompt, output.ToString());
            Assert.AreEqual('y', (char)input.Read());
            output.WriteLine("borrowed writer remains open");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RunAndPause_DirectWithNoArguments_PrintsUsageThenHoldsTheWindow()
    {
        using var output = new StringWriter();
        using var input = new StringReader("xy");
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            var host = new DirectHost(output, input, directory);

            var result = host.RunAndPause([], ownsWindow: true);

            Assert.AreEqual(2, result);
            var report = output.ToString();
            var usage = report.IndexOf("Usage:", StringComparison.Ordinal);
            var prompt = report.IndexOf(DirectHost.PausePrompt, StringComparison.Ordinal);
            Assert.IsTrue(usage >= 0 && usage < prompt, report);
            Assert.IsTrue(report.TrimEnd().EndsWith(DirectHost.PausePrompt, StringComparison.Ordinal), "The prompt is the last line before the key read.");
            Assert.AreEqual('y', (char)input.Read(), "Exactly one key was consumed.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RunAndPause_AShellOwnedConsole_DoesNotWait()
    {
        using var output = new StringWriter();
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            var host = new DirectHost(output, new FailingReader("A shell run must not wait for a key."), directory);

            Assert.AreEqual(2, host.RunAndPause([], ownsWindow: false));
            Assert.IsFalse(output.ToString().Contains(DirectHost.PausePrompt, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RunAndPause_Unattended_DoesNotWaitEvenOnAnOwnedWindow()
    {
        Environment.SetEnvironmentVariable(DirectHost.UnattendedVariableName, "1");
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            using var output = new StringWriter();
            using var input = new StringReader("x");
            var host = new DirectHost(output, input, directory);
            Assert.AreEqual(2, host.RunAndPause([], ownsWindow: true));
            Assert.AreEqual('x', (char)input.Read());
            Assert.IsFalse(output.ToString().Contains(DirectHost.PausePrompt, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RunAndPause_ParseErrorAndSuccess_BothHoldTheWindowSoTheyReadAlike()
    {
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            var dumpFile = MftDumpFixture.WriteFile(directory, MftDumpFixture.Standard());
            foreach (var (arguments, expectedExit) in new[]
                     {
                         (new[] { "no-such-verb" }, 2),
                         (new[] { "search", "D", "--source", "dump", "--dump-file", dumpFile, "--name", "notes" }, 0)
                     })
            {
                using var output = new StringWriter();
                using var input = new StringReader("xy");
                var host = new DirectHost(output, input, directory);

                Assert.AreEqual(expectedExit, host.RunAndPause(arguments, ownsWindow: true), output.ToString());

                StringAssert.EndsWith(output.ToString().TrimEnd(), DirectHost.PausePrompt, string.Join(' ', arguments));
                Assert.AreEqual('y', (char)input.Read(), $"{string.Join(' ', arguments)}: one key was consumed.");
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_ElevatedRelaunchTookOver_NamesTheOutputLogOnTheUnelevatedConsole(bool childSucceeded)
    {
        string[] arguments = ["scan", "C"];
        using var output = new StringWriter();
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            IReadOnlyList<string>? relaunchedWith = null;
            var elevationTimeout = TimeSpan.Zero;
            var host = new DirectHost(output, TextReader.Null, directory, new Microsoft.Extensions.Time.Testing.FakeTimeProvider())
            {
                _isElevated = () => false,
                _canSelfElevate = () => true,
                _tryRunElevated = (relaunchArguments, timeout) =>
                {
                    relaunchedWith = relaunchArguments;
                    elevationTimeout = timeout;
                    return childSucceeded;
                }
            };
            SampleHostElevationNoticeTests.AcknowledgeDeliberately(host);

            var result = host.RunWithElevation(arguments, ElevationNeed.SelfElevate, () => host.Run(arguments));

            Assert.AreEqual(childSucceeded ? 0 : 1, result);
            Assert.AreSequenceEqual(arguments, relaunchedWith!.ToArray());
            Assert.AreEqual(Timeout.InfiniteTimeSpan, elevationTimeout);
            var report = output.ToString();
            Assert.Contains($"The elevated run writes its output to {Path.Combine(directory, "output.log")}", report);
            if (!childSucceeded)
            {
                Assert.Contains("THE ELEVATED RUN DID NOT COMPLETE SUCCESSFULLY", report);
                Assert.Contains("output.log", report);
                Assert.IsFalse(report.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal), report);
                Assert.IsFalse(report.Contains("requires Administrative privileges", StringComparison.Ordinal), report);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void Run_NoElevatedRunIsAttempted_DoesNotNameAnOutputLogNothingWrites()
    {
        using var output = new StringWriter();
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            var host = new DirectHost(output, TextReader.Null, directory)
            {
                _isElevated = () => false,
                _canSelfElevate = () => false,
                _tryRunElevated = (_, _) => throw new AssertFailedException("No elevated run starts when the process cannot self-elevate.")
            };

            var result = host.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () => host.Run(["scan", "C"]));

            Assert.AreEqual(1, result);
            Assert.IsFalse(output.ToString().Contains("output.log", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RunAndPause_TheElevatedChildWritingToOutputLog_DoesNotHoldItsOwnWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Exercises Windows elevation log flow.");
            return;
        }

        using var output = new StringWriter();
        var directory = Directory.CreateTempSubdirectory("sample-log-").FullName;
        try
        {
            var host = new DirectHost(output, new FailingReader("The elevated child's window closes at once; the parent holds its own."), directory)
            {
                _isElevated = () => true
            };

            // A drive letter no volume uses: the elevated local scan fails at the open, and the failure goes to the log.
            var result = host.RunAndPause(["scan", UnusedDriveLetter()], ownsWindow: true);

            Assert.AreEqual(1, result);
            Assert.Contains("Error: ", File.ReadAllText(host.OutputLogPath));
            Assert.AreEqual(string.Empty, output.ToString());
            // The file is closed when the elevated operation returns.
            using (File.Open(host.OutputLogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    static string UnusedDriveLetter() =>
        char.ToString(Enumerable.Range('Q', 'Z' - 'Q' + 1).Select(letter => (char)letter)
            .First(letter => !Directory.Exists($"{letter}:\\")));

    [TestMethod]
    public void RunAndPause_UnwritableLogDirectory_PropagatesToFailureBoundaryAndLeavesWriterAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Exercises Windows elevation log flow.");
            return;
        }

        using var output = new StringWriter();
        var tempDir = Directory.CreateTempSubdirectory("sample-unwritable-").FullName;
        try
        {
            var filePath = Path.Combine(tempDir, "not-a-directory");
            File.WriteAllText(filePath, "blocker");
            var host = new DirectHost(output, TextReader.Null, filePath) { _isElevated = () => true };

            var exitCode = host.RunAndPause(["scan", "C"], ownsWindow: true);

            Assert.AreEqual(1, exitCode);
            Assert.Contains("Error: ", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void PhaseReporter_PrintsAgainEveryIntervalOfRowsWithinAPhaseAndNeverByTime()
    {
        using var output = new StringWriter();
        var reporter = new DirectHost.PhaseReporter(output);
        const uint interval = DirectHost.PhaseReporter.ProgressRowInterval;

        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, 0) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, interval - 1) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, interval) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, interval + 5) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, 2 * interval) { ExpectedRowCount = 3 * interval });

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            new[]
            {
                $"  D: ParsingMft: 0 rows of {3 * interval}",
                $"  D: ParsingMft: {interval} rows of {3 * interval}",
                $"  D: ParsingMft: {2 * interval} rows of {3 * interval}"
            }, lines);
    }
}
