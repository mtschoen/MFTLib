using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// A verb that throws must still print a readable failure and, on a double-clicked window, hold it open.
[TestClass]
[DoNotParallelize]
public class WatchHostFailureTests
{
    static SampleHost FailingHost(List<string> lines, Action readKey, bool ownsWindow)
    {
        return new SampleHost
        {
            _listDrives = () => throw new InvalidOperationException("drive listing failed", new IOException("device gone")),
            _getEnvironmentVariable = _ => null,
            _ownsConsoleWindow = () => ownsWindow,
            _readKey = readKey,
            _writeLine = lines.Add
        };
    }

    [TestMethod]
    public void RunAndPause_AVerbThrows_PrintsTheFailureReturnsOneAndHoldsTheWindow()
    {
        var lines = new List<string>();
        var keys = 0;
        var host = FailingHost(lines, () => keys++, ownsWindow: true);

        var result = host.RunAndPause(["rescan"]);

        var report = string.Join(Environment.NewLine, lines);
        Assert.AreEqual(1, result, report);
        Assert.IsTrue(lines.Contains("Error: InvalidOperationException: drive listing failed"), report);
        Assert.IsTrue(lines.Contains("  caused by IOException: device gone"), report);
        Assert.IsFalse(report.Contains("   at ", StringComparison.Ordinal), report);
        Assert.IsTrue(lines.Contains(SampleHost.PausePrompt), report);
        Assert.AreEqual(1, keys);
    }

    [TestMethod]
    public void RunAndPause_AVerbThrowsInAShellOwnedConsole_DoesNotWait()
    {
        var lines = new List<string>();
        var host = FailingHost(lines, () => throw new AssertFailedException("A shell run must not wait for a key."), ownsWindow: false);

        Assert.AreEqual(1, host.RunAndPause(["rescan"]));
        Assert.IsTrue(lines.Contains("Error: InvalidOperationException: drive listing failed"), string.Join(Environment.NewLine, lines));
    }
}
