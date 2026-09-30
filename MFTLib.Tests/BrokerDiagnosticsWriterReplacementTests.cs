using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The diagnostics writer is process-global, so the class runs serially and restores the defaults.
[TestClass]
[DoNotParallelize]
public class BrokerDiagnosticsWriterReplacementTests
{
    [TestCleanup]
    public void Cleanup()
    {
        BrokerDiagnostics.ResetToDefaults();
    }

    [TestMethod]
    public async Task ReplaceWriterForTest_NoWriterYet_RoutesLogLinesIntoTheReplacement()
    {
        BrokerDiagnostics.ResetToDefaults();
        var lines = new List<string>();

        // Diagnostics are still off, so nothing has created the process's writer: the replacement
        // is the first.
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(lines.Add, () => "broker"));
        BrokerDiagnostics.Enable("broker");
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "hello");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, lines.Count);
        StringAssert.Contains(lines[0], ":control]  hello");
    }

    [TestMethod]
    public async Task ReplaceWriterForTest_ReplacingALiveWriter_RoutesLaterLinesOnlyToTheReplacement()
    {
        BrokerDiagnostics.ResetToDefaults();
        var first = new List<string>();
        var second = new List<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(first.Add, () => "broker"));
        BrokerDiagnostics.Enable("broker");

        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(second.Add, () => "broker"));
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "after");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, second.Count);
        StringAssert.Contains(second[0], ":control]  after");
        Assert.AreEqual(0, first.Count, "The replaced writer was completed and receives nothing more.");
    }
}
