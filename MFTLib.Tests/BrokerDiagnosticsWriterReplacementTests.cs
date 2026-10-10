using System.Collections.Concurrent;
using MFTLibTestExtensions;
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
        BrokerDiagnostics.Enable("broker", Path.GetTempPath());
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
        BrokerDiagnostics.Enable("broker", Path.GetTempPath());

        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(second.Add, () => "broker"));
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "after");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, second.Count);
        StringAssert.Contains(second[0], ":control]  after");
        Assert.AreEqual(0, first.Count, "The replaced writer was completed and receives nothing more.");
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Log_AcquiredWriterIsReplaced_DeliversOnceToCurrentWriter(int replacementCount)
    {
        BrokerDiagnostics.ResetToDefaults();
        var gate = new TestGate();
        var originalLines = new ConcurrentQueue<string>();
        var replacementLines = new ConcurrentQueue<string>();
        var original = new BrokerDiagnosticsWriter(originalLines.Enqueue);
        BrokerDiagnostics.ReplaceWriterForTest(original);
        BrokerDiagnostics.Enable("broker", Path.GetTempPath());
        BrokerDiagnostics.AfterWriterAcquiredForTest = () =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
        };

        var logging = Task.Run(() =>
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "acquired-before-replacement"));
        try
        {
            await gate.Entered.WaitAsync(ScriptedWatchSource.HangGuard);
            for (var index = 0; index < replacementCount; index++)
            {
                BrokerDiagnostics.ReplaceWriterForTest(
                    new BrokerDiagnosticsWriter(replacementLines.Enqueue));
            }

            gate.Release();
            await logging.WaitAsync(ScriptedWatchSource.HangGuard);
            await BrokerDiagnostics.FlushAsync(CancellationToken.None)
                .WaitAsync(ScriptedWatchSource.HangGuard);
            await original.FlushAsync(CancellationToken.None)
                .WaitAsync(ScriptedWatchSource.HangGuard);

            var delivered = replacementLines.ToArray();
            Assert.AreEqual(1, delivered.Length,
                "A line acquired before replacement must reach the current writer exactly once.");
            StringAssert.Contains(delivered[0], ":control]  acquired-before-replacement");
            Assert.AreEqual(0, originalLines.Count,
                "The completed original writer must not receive the delayed line.");
        }
        finally
        {
            gate.Release();
            try
            {
                await logging.WaitAsync(ScriptedWatchSource.HangGuard);
            }
            finally
            {
                BrokerDiagnostics.ResetToDefaults();
            }
        }
    }
}
