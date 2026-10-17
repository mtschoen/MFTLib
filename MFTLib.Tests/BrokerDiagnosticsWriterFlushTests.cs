using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Each test owns its writer instance, so nothing process-global is touched.
[TestClass]
public class BrokerDiagnosticsWriterFlushTests
{
    [TestMethod]
    public async Task Append_QueueDropsAndRepeatedAppendFailures_RetainsBothCausesUntilRecovery()
    {
        var gate = new TestGate();
        var lines = new List<string>();
        var offline = new System.Runtime.CompilerServices.StrongBox<bool>(true);
        var writer = new BrokerDiagnosticsWriter(line =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
            if (offline.Value)
            {
                throw new IOException("sink unavailable");
            }

            lines.Add(line);
        });
        try
        {
            Assert.IsTrue(writer.TryEnqueue("held record"));
            await gate.Entered.WaitAsync(HangGuard);
            for (var index = 0; index < BrokerDiagnosticsWriter.Capacity; index++)
            {
                Assert.IsTrue(writer.TryEnqueue("queued record"));
            }

            Assert.IsFalse(writer.TryEnqueue("queue overflow"));
            gate.Release();
            await writer.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            offline.Value = false;
            Assert.IsTrue(writer.TryEnqueue("recovered record"));
            await writer.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

            Assert.AreEqual(2, lines.Count);
            StringAssert.Contains(lines[0], $"{BrokerDiagnosticsWriter.Capacity + 2} records dropped: queue full (1); append failed ({BrokerDiagnosticsWriter.Capacity + 1}): IOException: sink unavailable");
            Assert.AreEqual("recovered record", lines[1]);
        }
        finally
        {
            gate.Release();
            writer.Complete();
            await writer.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        }
    }

    [TestMethod]
    public async Task FlushAsync_AfterRefusedEnqueue_WaitsForAllAcceptedLines()
    {
        var firstAppend = new TestGate();
        var lastAppend = new TestGate();
        var lines = new List<string>();
        var lastLine = $"fill {BrokerDiagnosticsWriter.Capacity - 1}";
        var writer = new BrokerDiagnosticsWriter(line =>
        {
            if (line == "parks the sink")
            {
                firstAppend.MarkEntered();
                firstAppend.WaitForRelease();
            }
            if (line == lastLine)
            {
                lastAppend.MarkEntered();
                lastAppend.WaitForRelease();
            }
            lines.Add(line);
        });
        try
        {
            Assert.IsTrue(writer.TryEnqueue("parks the sink"));
            await firstAppend.Entered.WaitAsync(HangGuard);
            for (var index = 0; index < BrokerDiagnosticsWriter.Capacity; index++)
            {
                Assert.IsTrue(writer.TryEnqueue($"fill {index}"));
            }
            Assert.IsFalse(writer.TryEnqueue("dropped"));
            var flush = writer.FlushAsync(CancellationToken.None);
            Assert.IsFalse(flush.IsCompleted);
            firstAppend.Release();
            await lastAppend.Entered.WaitAsync(HangGuard);
            Assert.IsFalse(flush.IsCompleted);
            lastAppend.Release();
            await flush.WaitAsync(HangGuard);
            Assert.AreEqual(BrokerDiagnosticsWriter.Capacity + 2, lines.Count);
            Assert.AreEqual(1, lines.Count(line =>
                line.Contains("1 records dropped: queue full", StringComparison.Ordinal)));
            Assert.IsFalse(lines.Contains("dropped"));
            CollectionAssert.AreEqual(
                new[] { "parks the sink" }.Concat(Enumerable.Range(0, BrokerDiagnosticsWriter.Capacity)
                    .Select(index => $"fill {index}")).ToArray(),
                lines.Where(line => !line.Contains("records dropped:", StringComparison.Ordinal)).ToArray());
        }
        finally
        {
            firstAppend.Release();
            lastAppend.Release();
            writer.Complete();
            await writer.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        }
    }

    [TestMethod]
    public async Task TryEnqueue_AfterComplete_RefusesTheRecordAndFlushCompletes()
    {
        var lines = new List<string>();
        var writer = new BrokerDiagnosticsWriter(lines.Add);
        writer.Complete();

        Assert.IsFalse(writer.TryEnqueue("late"));
        await writer.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(0, lines.Count);
    }
}
