using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// A verb that throws must still print a readable failure and, on a double-clicked window, hold it open.
[TestClass]
[DoNotParallelize]
public class WatchHostFailureTests
{
    static SampleHost FailingHost(List<string> lines, TextReader input)
    {
        return new SampleHost(new LineCollectingWriter(lines), input)
        {
            _isElevated = () => throw new InvalidOperationException("drive listing failed", new IOException("device gone")),
        };
    }

    [TestMethod]
    public void RunAndPause_AVerbThrows_PrintsTheFailureReturnsOneAndHoldsTheWindow()
    {
        using var attended = new UnattendedEnvironmentScope(null);
        var lines = new List<string>();
        using var input = new StringReader("x");
        var host = FailingHost(lines, input);

        var result = host.RunAndPause(["rescan", "C", "--cache-directory", "cache"]);

        var report = string.Join(Environment.NewLine, lines);
        Assert.AreEqual(1, result, report);
        Assert.IsTrue(lines.Contains("Error: InvalidOperationException: drive listing failed"), report);
        Assert.IsTrue(lines.Contains("  caused by IOException: device gone"), report);
        Assert.IsFalse(report.Contains("   at ", StringComparison.Ordinal), report);

        host.PauseBeforeExit(ownsWindow: true);
        var pausedReport = string.Join(Environment.NewLine, lines);
        Assert.IsTrue(lines.Contains(SampleHost.PausePrompt), pausedReport);
        Assert.AreEqual(-1, input.Read());
    }

    [TestMethod]
    public void RunAndPause_AVerbThrowsInAShellOwnedConsole_DoesNotWait()
    {
        var lines = new List<string>();
        var host = FailingHost(lines, new FailingReader("A shell run must not wait for a key."));

        var result = host.RunAndPause(["rescan", "C", "--cache-directory", "cache"]);
        Assert.AreEqual(1, result);

        host.PauseBeforeExit(ownsWindow: false);
        Assert.IsTrue(lines.Contains("Error: InvalidOperationException: drive listing failed"), string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Contains(SampleHost.PausePrompt));
    }
}
