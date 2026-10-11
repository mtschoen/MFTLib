using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DirectHost = SampleProgram.Direct.SampleHost;

namespace MFTLib.Tests;

// What a double-clicked sample does with no arguments: the Direct usage and the window hold shared by both samples,
// and the output.log notice of an elevated relaunch. The consoles are seams; nothing here touches a real window.
[TestClass]
[DoNotParallelize]
public class SampleHostFirstRunTests
{
    [TestMethod]
    public void RunAndPause_DirectWithNoArguments_PrintsUsageThenHoldsTheWindow()
    {
        var events = new List<string>();
        var host = new DirectHost
        {
            _getEnvironmentVariable = _ => null,
            _ownsConsoleWindow = () => true,
            _readKey = () => events.Add("key"),
            _writeLine = events.Add
        };

        var result = host.RunAndPause([]);

        Assert.AreEqual(2, result);
        var usage = events.FindIndex(line => line.StartsWith("Usage:", StringComparison.Ordinal));
        var prompt = events.IndexOf(DirectHost.PausePrompt);
        var key = events.IndexOf("key");
        Assert.IsTrue(usage >= 0 && usage < prompt && prompt < key, string.Join(Environment.NewLine, events));
        Assert.AreEqual(events.Count - 1, key, "The key read ends the run.");
    }

    [TestMethod]
    public void RunAndPause_AShellOwnedConsole_DoesNotWait()
    {
        var host = new DirectHost
        {
            _getEnvironmentVariable = _ => null,
            _ownsConsoleWindow = () => false,
            _readKey = () => throw new AssertFailedException("A shell run must not wait for a key."),
            _writeLine = _ => { }
        };

        Assert.AreEqual(2, host.RunAndPause([]));
    }

    [TestMethod]
    public void RunAndPause_Unattended_DoesNotWaitEvenOnAnOwnedWindow()
    {
        var host = new DirectHost
        {
            _getEnvironmentVariable = name => name == DirectHost.UnattendedVariableName ? "1" : null,
            _ownsConsoleWindow = () => throw new AssertFailedException("Unattended runs never ask about the window."),
            _readKey = () => throw new AssertFailedException("An unattended run must not wait for a key."),
            _writeLine = _ => { }
        };

        Assert.AreEqual(2, host.RunAndPause([]));
    }

    [TestMethod]
    public void RunAndPause_ParseErrorAndSuccess_BothHoldTheWindowSoTheyReadAlike()
    {
        var keys = 0;
        var host = new DirectHost
        {
            _getEnvironmentVariable = _ => null,
            _ownsConsoleWindow = () => true,
            _readKey = () => keys++,
            _writeLine = _ => { }
        };

        Assert.AreEqual(2, host.RunAndPause(["no-such-verb"]));
        Assert.AreEqual(1, keys);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_ElevatedRelaunchTookOver_NamesTheOutputLogOnTheUnelevatedConsole(bool childSucceeded)
    {
        var lines = new List<string>();
        var host = new DirectHost
        {
            _isWindows = () => true,
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => childSucceeded,
            _getEnvironmentVariable = _ => null,
            _writeLine = lines.Add
        };
        SampleHostElevationNoticeTests.AcknowledgeDeliberately(host);

        var result = host.Run(["scan", "C"]);

        Assert.AreEqual(childSucceeded ? 0 : 1, result);
        Assert.IsTrue(lines.Contains($"The elevated run writes its output to {Path.Combine(AppContext.BaseDirectory, "output.log")}"), string.Join(Environment.NewLine, lines));
        if (!childSucceeded)
        {
            Assert.IsTrue(lines.Exists(line => line.Contains("THE ELEVATED RUN DID NOT COMPLETE SUCCESSFULLY", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
            Assert.IsTrue(lines.Exists(line => line.Contains("If the elevated run started, its output is in", StringComparison.Ordinal) && line.Contains("output.log", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
            Assert.IsFalse(lines.Exists(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
            Assert.IsFalse(lines.Exists(line => line.Contains("requires Administrative privileges", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        }
    }

    [TestMethod]
    public void Run_NoElevatedRunIsAttempted_DoesNotNameAnOutputLogNothingWrites()
    {
        var lines = new List<string>();
        var host = new DirectHost
        {
            _isWindows = () => true,
            _isElevated = () => false,
            _canSelfElevate = () => false,
            _tryRunElevated = (_, _) => throw new AssertFailedException("No elevated run starts when the process cannot self-elevate."),
            _getEnvironmentVariable = _ => null,
            _writeLine = lines.Add
        };

        Assert.AreEqual(1, host.Run(["scan", "C"]));
        Assert.IsFalse(lines.Exists(line => line.Contains("output.log")), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void RunAndPause_TheElevatedChildWritingToOutputLog_DoesNotHoldItsOwnWindow()
    {
        var host = new DirectHost
        {
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _isWindows = () => true,
            _getEnvironmentVariable = _ => null,
            _ownsConsoleWindow = () => true,
            _readKey = () => throw new AssertFailedException("The elevated child's window closes at once; the parent holds its own."),
            _writeLine = _ => { },
            _resolveDrive = _ => throw new IOException("Mock: no volume"),
            _createSource = _ => throw new IOException("Mock: no volume")
        };

        Assert.AreEqual(1, host.RunAndPause(["scan", "C"]));
    }

    [TestMethod]
    public void PhaseReporter_PrintsAgainEveryIntervalOfRowsWithinAPhaseAndNeverByTime()
    {
        var lines = new List<string>();
        var reporter = new DirectHost.PhaseReporter(lines.Add);
        const uint interval = DirectHost.PhaseReporter.ProgressRowInterval;

        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, 0) { TotalRows = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, interval - 1) { TotalRows = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, interval) { TotalRows = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, interval + 5) { TotalRows = 3 * interval });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, 2 * interval) { TotalRows = 3 * interval });

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
