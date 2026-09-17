using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProtocolTests
{
    [TestMethod]
    public void WireBytes_Golden_StartWatchFrame_WithArmEpoch()
    {
        AssertWireBytes(w => BrokerProtocol.WriteStartWatch(w, "C:7:100:1"),
        [
            0x17, 0x00, 0x00, 0x00, // totalLength = 23
            0x02, // kind = StartWatch
            0x12, 0x00, 0x00, 0x00, // drivesSpec length = 18
            0x43, 0x00, 0x3A, 0x00, 0x37, 0x00, 0x3A, 0x00, 0x31, 0x00,
            0x30, 0x00, 0x30, 0x00, 0x3A, 0x00, 0x31, 0x00 // "C:7:100:1"
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_JournalBatchFrame_WithArmEpoch()
    {
        AssertWireBytes(
            w => BrokerProtocol.WriteJournalBatch(w, "C", 3U, new UsnJournalCursor(1UL, 2L),
                Array.Empty<UsnJournalEntry>()),
            [
                0x1F, 0x00, 0x00, 0x00, // totalLength = 31
                0x06, // kind = JournalBatch
                0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
                0x03, 0x00, 0x00, 0x00, // armEpoch = 3
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 2
                0x00, 0x00, 0x00, 0x00 // entryCount = 0
            ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ErrorFrame_WithArmEpoch()
    {
        AssertWireBytes(w => BrokerProtocol.WriteError(w, "C", 42U, "D"),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x07, // kind = Error
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x2A, 0x00, 0x00, 0x00, // armEpoch = 42
            0x02, 0x00, 0x00, 0x00, 0x44, 0x00 // message "D"
        ]);
    }

    [TestMethod]
    public void JournalBatchFrame_RoundTripsItsArmEpoch()
    {
        var cursor = new UsnJournalCursor(91UL, 1234L);
        var entry = JournalEntryFactory.Create(9, 14, "café.txt", UsnReason.FileCreate | UsnReason.Close);
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(buffer, "D:\\", uint.MaxValue, cursor, [entry]);

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(buffer.WrittenCount, consumed);
        Assert.AreEqual("D:\\", frame.Drive);
        Assert.AreEqual(uint.MaxValue, frame.ArmEpoch);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(1, frame.Entries.Length);
        Assert.AreEqual(entry, frame.Entries[0]);
    }

    [TestMethod]
    public void ErrorFrame_RoundTripsItsArmEpoch()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(buffer, "D:\\", uint.MaxValue, "journal enveloppé");

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(buffer.WrittenCount, consumed);
        Assert.AreEqual("D:\\", frame.Drive);
        Assert.AreEqual(uint.MaxValue, frame.ArmEpoch);
        Assert.AreEqual("journal enveloppé", frame.Message);
    }

    [TestMethod]
    public void CaughtUpFrame_RoundTripsItsDriveAndArmEpoch()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteCaughtUp(buffer, "D:\\", uint.MaxValue);

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);
        Assert.AreEqual(buffer.WrittenCount, consumed);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, frame.Kind);
        Assert.AreEqual("D:\\", frame.Drive);
        Assert.AreEqual(uint.MaxValue, frame.ArmEpoch);
    }

    [TestMethod]
    public void WireBytes_Golden_CaughtUpFrame_WithArmEpoch()
    {
        // [totalLength int32][kind byte][driveByteLen int32][drive UTF-16][armEpoch uint32]
        // totalLength = kind(1) + len(4) + "D"(2) + epoch(4) = 11; the drive length is a byte
        // count, matching WriteError/WriteJournalBatch (driveBytes.Length), not a char count.
        byte[] expected =
        [
            0x0B, 0x00, 0x00, 0x00,
            0x12, // CaughtUp follows the existing journal-size request (16) and reply (17).
            0x02, 0x00, 0x00, 0x00,
            0x44, 0x00,
            0x2A, 0x00, 0x00, 0x00
        ];

        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCaughtUp(buffer, "D", 42);
        CollectionAssert.AreEqual(expected, buffer.WrittenSpan.ToArray());

        var frame = BrokerProtocol.ReadFrame(expected, out var consumed);
        Assert.AreEqual(expected.Length, consumed);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, frame.Kind);
        Assert.AreEqual("D", frame.Drive);
        Assert.AreEqual(42U, frame.ArmEpoch);
    }

    [TestMethod]
    public void NoArmEpoch_IsZeroSoAScanFrameCanNeverMatchALiveArm()
    {
        Assert.AreEqual(0U, BrokerFrame.NoArmEpoch);
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(buffer, "C", BrokerFrame.NoArmEpoch, "scan failed");

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);

        Assert.AreEqual(0U, frame.ArmEpoch);
    }
}
