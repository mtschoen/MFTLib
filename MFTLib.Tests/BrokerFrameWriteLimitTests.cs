using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The frame length limit holds on the write side too: a writer never emits a prefix the reader
// would refuse, and each oversized input has a defined outcome.
[TestClass]
public class BrokerFrameWriteLimitTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);

    static string[] KeepList(int count) => [.. Enumerable.Repeat(new string('n', 255), count)];

    [TestMethod]
    public void WriteArmAndScan_KeepListOverTheLimit_ThrowsBeforeWritingAnything()
    {
        var buffer = new ArrayBufferWriter<byte>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("section", KeepList(32_700))));

        Assert.Contains("frame limit", exception.Message);
        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void WriteJournalBatch_OverTheLimit_ThrowsBeforeWritingAnything()
    {
        var entries = Enumerable.Range(0, 30_500)
            .Select(index => JournalEntries.Create((ulong)index, index, new string('e', 255))).ToArray();
        var buffer = new ArrayBufferWriter<byte>();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrokerProtocol.WriteJournalBatch(buffer, BrokerFrame.JournalBatch(Armed, entries)));

        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(new string[0])]
    [DataRow(new[] { ".git", "HEAD" })]
    public void ArmAndScanFrameLength_MatchesTheWrittenFrame(string[]? names)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("section-C", names));

        Assert.AreEqual(BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan),
            BrokerProtocol.ArmAndScanFrameLength("section-C", names));
    }

    [TestMethod]
    public void WriteError_MessageOverTheLimit_IsCutAndMarkedToFit()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteError(buffer, BrokerFrame.Error(3, new string('x', 8_388_604)));

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(BrokerProtocol.MaximumMessageUnits, frame.RequireMessage().Length);
        Assert.IsTrue(frame.RequireMessage().EndsWith(BrokerProtocol.TruncationMarker, StringComparison.Ordinal));
        Assert.IsLessThan(BrokerFrameStream.MaximumFrameLength, consumed);
    }

    [TestMethod]
    public void WriteStalled_MessageOverTheLimit_IsCutAndMarkedToFit()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteStalled(buffer, BrokerFrame.Stalled(new string('x', 9_000_000)));

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);
        Assert.AreEqual(BrokerProtocol.MaximumMessageUnits, frame.RequireMessage().Length);
    }

    [TestMethod]
    public void WriteError_MessageWithinTheLimit_IsUnchanged()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteError(buffer, BrokerFrame.Error(3, "journal wrapped"));

        Assert.AreEqual("journal wrapped", BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _).RequireMessage());
    }
}
