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
    public async Task Cleanup()
    {
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        BrokerDiagnostics.Dependencies.ResetToDefaults();
    }

    [TestMethod]
    public async Task InstallWriter_NoWriterYet_RoutesLogLinesIntoTheReplacement()
    {
        BrokerDiagnostics.Dependencies.ResetToDefaults();
        var lines = new List<string>();
        var writer = new BrokerDiagnosticsWriter(lines.Add, () => "broker");
        try
        {
            // Diagnostics are still off, so nothing has created the process's writer: the replacement
            // is the first.
            BrokerDiagnostics.Dependencies.InstallWriter(writer);
            BrokerDiagnostics.Enable("broker", Path.GetTempPath());
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "hello");
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

            Assert.HasCount(1, lines);
            Assert.Contains(":control]  hello", lines[0]);
        }
        finally
        {
            writer.Complete();
            await writer.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task InstallWriter_ReplacingALiveWriter_RoutesLaterLinesOnlyToTheReplacement()
    {
        BrokerDiagnostics.Dependencies.ResetToDefaults();
        var first = new List<string>();
        var second = new List<string>();
        var firstWriter = new BrokerDiagnosticsWriter(first.Add, () => "broker");
        var secondWriter = new BrokerDiagnosticsWriter(second.Add, () => "broker");
        try
        {
            BrokerDiagnostics.Dependencies.InstallWriter(firstWriter);
            BrokerDiagnostics.Enable("broker", Path.GetTempPath());

            BrokerDiagnostics.Dependencies.InstallWriter(secondWriter);
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "after");
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

            Assert.HasCount(1, second);
            Assert.Contains(":control]  after", second[0]);
            Assert.IsEmpty(first, "The replaced writer was completed and receives nothing more.");
        }
        finally
        {
            firstWriter.Complete();
            secondWriter.Complete();
            await Task.WhenAll(firstWriter.FlushAsync(CancellationToken.None), secondWriter.FlushAsync(CancellationToken.None))
                .WaitAsync(HangGuard);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Log_BeforeAndAfterReplacement_DeliversEachLineExactlyOnce(int replacementCount)
    {
        BrokerDiagnostics.Dependencies.ResetToDefaults();
        var gate = new TestGate();
        var writers = new List<BrokerDiagnosticsWriter>();
        var outputs = new List<ConcurrentQueue<string>>();
        var installation = Task.CompletedTask;
        BrokerDiagnostics.Enable("broker", Path.GetTempPath());
        try
        {
            for (var index = 0; index <= replacementCount; index++)
            {
                var output = new ConcurrentQueue<string>();
                var holdSink = index == 0;
                var writer = new BrokerDiagnosticsWriter(line =>
                {
                    if (holdSink)
                    {
                        gate.MarkEntered();
                        gate.WaitForRelease();
                    }
                    output.Enqueue(line);
                });
                writers.Add(writer);
                outputs.Add(output);
                installation = Task.Run(() => BrokerDiagnostics.Dependencies.InstallWriter(writer));
                await installation.WaitAsync(HangGuard);
                BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, $"writer-{index}");
                if (index == 0)
                {
                    await gate.Entered.WaitAsync(HangGuard);
                }
            }
            await writers[^1].FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            Assert.IsEmpty(outputs[0]);
            gate.Release();
            await Task.WhenAll(writers.Select(writer => writer.FlushAsync(CancellationToken.None)))
                .WaitAsync(HangGuard);
            for (var index = 0; index < writers.Count; index++)
            {
                Assert.HasCount(1, outputs[index]);
                StringAssert.EndsWith(outputs[index].Single(), $":control]  writer-{index}");
            }
            foreach (var retired in writers.Take(replacementCount))
            {
                Assert.IsFalse(retired.TryEnqueue("late"));
            }
        }
        finally
        {
            gate.Release();
            await installation.WaitAsync(HangGuard);
            foreach (var writer in writers)
            {
                writer.Complete();
            }
            await Task.WhenAll(writers.Select(writer => writer.FlushAsync(CancellationToken.None)))
                .WaitAsync(HangGuard);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
        }
    }
}
