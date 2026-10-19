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
        using var input = new StringReader("xy");
        var host = FailingHost(lines, input);

        var result = host.RunAndPause(["rescan", "C", "--cache-directory", "cache"], ownsWindow: true);

        var report = string.Join(Environment.NewLine, lines);
        Assert.AreEqual(1, result, report);
        Assert.IsTrue(lines.Contains("Error: InvalidOperationException: drive listing failed"), report);
        Assert.IsTrue(lines.Contains("  caused by IOException: device gone"), report);
        Assert.IsFalse(report.Contains("   at ", StringComparison.Ordinal), report);
        Assert.AreEqual(SampleHost.PausePrompt, lines[^1], "The prompt follows the failure and is the last line before the key read.");
        Assert.AreEqual('y', (char)input.Read(), "Exactly one key was consumed.");
    }

    [TestMethod]
    public void RunAndPause_AVerbThrowsInAShellOwnedConsole_DoesNotWait()
    {
        using var attended = new UnattendedEnvironmentScope(null);
        var lines = new List<string>();
        var host = FailingHost(lines, new FailingReader("A shell run must not wait for a key."));

        var result = host.RunAndPause(["rescan", "C", "--cache-directory", "cache"], ownsWindow: false);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Contains("Error: InvalidOperationException: drive listing failed"), string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Contains(SampleHost.PausePrompt));
    }

    [TestMethod]
    public void RunAndPause_UsageErrorOnAnOwnedWindow_PrintsUsageThenHoldsTheWindow()
    {
        using var attended = new UnattendedEnvironmentScope(null);
        var lines = new List<string>();
        using var input = new StringReader("xy");
        var host = new SampleHost(new LineCollectingWriter(lines), input);

        var result = host.RunAndPause(["--no-such-option"], ownsWindow: true);

        var report = string.Join(Environment.NewLine, lines);
        Assert.AreEqual(2, result, report);
        var usage = lines.FindIndex(line => line.StartsWith("Usage:", StringComparison.Ordinal));
        Assert.IsTrue(usage >= 0 && usage < lines.IndexOf(SampleHost.PausePrompt), report);
        Assert.AreEqual('y', (char)input.Read(), "Exactly one key was consumed.");
    }

    [TestMethod]
    public void RunAndPause_Unattended_DoesNotWaitEvenOnAnOwnedWindow()
    {
        using var unattended = new UnattendedEnvironmentScope("1");
        var lines = new List<string>();
        var host = FailingHost(lines, new FailingReader("An unattended run must not wait for a key."));

        Assert.AreEqual(1, host.RunAndPause(["rescan", "C", "--cache-directory", "cache"], ownsWindow: true));
        Assert.IsFalse(lines.Contains(SampleHost.PausePrompt));
    }
}
