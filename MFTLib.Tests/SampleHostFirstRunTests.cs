using MFTLib.Index;
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
            StringAssert.Contains(output.ToString(), DirectHost.PausePrompt);
            Assert.AreEqual('y', (char)input.Read());
            output.WriteLine("borrowed writer remains open");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RunAndPause_DirectWithNoArguments_PrintsUsage()
    {
        using var output = new StringWriter();
        var directory = Directory.CreateTempSubdirectory("sample-first-run-").FullName;
        try
        {
            var host = new DirectHost(output, TextReader.Null, directory);
            var result = host.RunAndPause([]);
            Assert.AreEqual(2, result);
            StringAssert.Contains(output.ToString(), "Usage:");
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
            Assert.AreEqual(2, host.RunAndPause([]));
            Assert.AreEqual('x', (char)input.Read());
            Assert.IsFalse(output.ToString().Contains(DirectHost.PausePrompt, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
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
            CollectionAssert.AreEqual(arguments, relaunchedWith!.ToArray());
            Assert.AreEqual(Timeout.InfiniteTimeSpan, elevationTimeout);
            var report = output.ToString();
            StringAssert.Contains(report, $"The elevated run writes its output to {Path.Combine(directory, "output.log")}");
            if (!childSucceeded)
            {
                StringAssert.Contains(report, "THE ELEVATED RUN DID NOT COMPLETE SUCCESSFULLY");
                StringAssert.Contains(report, "output.log");
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
        using var output = new StringWriter();
        using var input = new StringReader("x");
        var directory = Directory.CreateTempSubdirectory("sample-log-").FullName;
        try
        {
            var host = new DirectHost(output, input, directory) { _isElevated = () => true };
            Assert.AreEqual(2, host.RunWithElevation([], ElevationNeed.SelfElevate, () => host.Run([])));
            var path = Path.Combine(directory, "output.log");
            StringAssert.Contains(File.ReadAllText(path), "Usage:");
            Assert.AreEqual(string.Empty, output.ToString());
            Assert.AreEqual('x', (char)input.Read());
            // The file is closed when the elevated operation returns.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.AreEqual(2, host.Run([]));
            StringAssert.Contains(output.ToString(), "Usage:");

            // Operation failure writes to the log, returns exit code 1, and leaves borrowed writer untouched.
            Assert.AreEqual(1, host.RunWithElevation([], ElevationNeed.SelfElevate, () => throw new IOException("operation failed")));
            StringAssert.Contains(File.ReadAllText(path), "Error: IOException: operation failed");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

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

            var exitCode = host.RunAndPause(["scan", "C"]);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(output.ToString(), "Error: ");
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
        CollectionAssert.AreEqual(
            new[]
            {
                $"  D: ParsingMft: 0 rows of {3 * interval}",
                $"  D: ParsingMft: {interval} rows of {3 * interval}",
                $"  D: ParsingMft: {2 * interval} rows of {3 * interval}"
            },
            lines);
    }
}
