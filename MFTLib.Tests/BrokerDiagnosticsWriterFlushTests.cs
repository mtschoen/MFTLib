using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Each test owns its writer instance, so nothing process-global is touched.
[TestClass]
public class BrokerDiagnosticsWriterFlushTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

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
    public async Task FlushAsync_TakenWhileARecordIsBeingDropped_CompletesOnceTheQueuedRecordsAreAppended()
    {
        var gate = new TestGate();
        var lines = new List<string>();
        var writer = new BrokerDiagnosticsWriter(line =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
            lines.Add(line);
        });
        Assert.IsTrue(writer.TryEnqueue("parks the sink"));
        await gate.Entered.WaitAsync(HangGuard);
        for (var index = 0; index < BrokerDiagnosticsWriter.Capacity; index++)
        {
            Assert.IsTrue(writer.TryEnqueue("fill " + index));
        }

        // The flush is taken from inside the drop, the moment the dropped record's accounting is
        // still open: it must wait only for the records that were really queued.
        Task? flush = null;
        writer.BeforeDropAccountedForTest = () => flush ??= writer.FlushAsync(CancellationToken.None);
        Assert.IsFalse(writer.TryEnqueue("dropped"));
        Assert.IsNotNull(flush);

        gate.Release();
        await flush.WaitAsync(HangGuard);

        // Every queued record, plus the report of the one that was dropped.
        Assert.AreEqual(1 + BrokerDiagnosticsWriter.Capacity + 1, lines.Count);
        Assert.AreEqual(1, lines.Count(line => line.Contains("1 records dropped: queue full", StringComparison.Ordinal)));
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
