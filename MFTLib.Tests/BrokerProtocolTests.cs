using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerProtocolTests
{
    [TestMethod]
    public void JournalEntry_RoundTrips_AllFields()
    {
        var entry = UsnJournalEntry.Create(
            recordNumber: 42, parentRecordNumber: 7, usn: 123456,
            timestamp: new DateTime(2026, 6, 20, 1, 2, 3, DateTimeKind.Utc),
            reason: UsnReason.FileCreate | UsnReason.Close,
            fileAttributes: FileAttributes.Archive,
            fileName: "repört.txt"); // non-ASCII to prove UTF-16

        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEntry(buffer, entry);
        var read = BrokerProtocol.ReadEntry(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(buffer.WrittenCount, consumed);
        Assert.AreEqual(entry.RecordNumber, read.RecordNumber);
        Assert.AreEqual(entry.ParentRecordNumber, read.ParentRecordNumber);
        Assert.AreEqual(entry.Usn, read.Usn);
        Assert.AreEqual(entry.Timestamp, read.Timestamp);
        Assert.AreEqual(entry.Reason, read.Reason);
        Assert.AreEqual(entry.FileAttributes, read.FileAttributes);
        Assert.AreEqual(entry.FileName, read.FileName);
    }

    [TestMethod]
    public void JournalBatchFrame_RoundTrips_EntriesAndCursor()
    {
        var entries = new[]
        {
            UsnJournalEntry.Create(1, 5, 10, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "a"),
            UsnJournalEntry.Create(2, 5, 20, DateTime.UnixEpoch, UsnReason.FileDelete | UsnReason.Close, FileAttributes.Normal, "b"),
        };
        var cursor = new UsnJournalCursor(99UL, 20L);

        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(buffer, "C:\\", cursor, entries);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual("C:\\", frame.Drive);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(2, frame.Entries.Length);
        Assert.AreEqual("b", frame.Entries[1].FileName);
    }

    [TestMethod]
    public void ScanReadyFrame_RoundTrips_MmfHandshake()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteScanReady(buffer, mmfName: "mftlib-scan-123", recordCount: 8_000_000, byteLength: 900_000_000);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);

        Assert.AreEqual(BrokerFrameKind.ScanReady, frame.Kind);
        Assert.AreEqual("mftlib-scan-123", frame.MmfName);
        Assert.AreEqual(8_000_000, frame.RecordCount);
        Assert.AreEqual(900_000_000L, frame.ByteLength);
    }

    [TestMethod]
    public void ErrorFrame_RoundTrips_PerDriveMessage()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(buffer, "D:\\", "journal wrapped");
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("D:\\", frame.Drive);
        Assert.AreEqual("journal wrapped", frame.Message);
    }

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_DrivesSpec()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "C:0:0,D:7:42");
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("C:0:0,D:7:42", frame.DrivesSpec);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void StartWatchFrame_RoundTrips_DrivesSpec()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(buffer, "C:1:100");
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);
        Assert.AreEqual("C:1:100", frame.DrivesSpec);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void ShutdownFrame_RoundTrips_NoPayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(buffer);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.Shutdown, frame.Kind);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void HeartbeatFrame_RoundTrips_NoPayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteHeartbeat(buffer);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.Heartbeat, frame.Kind);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void EndWatchFrame_RoundTrips_NoPayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatch(buffer);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.EndWatch, frame.Kind);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void EndWatchAckFrame_RoundTrips_NoPayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(buffer);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck, frame.Kind);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void CursorFrame_RoundTrips_DriveAndCursor()
    {
        var cursor = new UsnJournalCursor(12345UL, 67890L);
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCursor(buffer, "E:\\", cursor);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.Cursor, frame.Kind);
        Assert.AreEqual("E:\\", frame.Drive);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void ReadFrame_SetsConsumedToFullFrameLength()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(buffer, "C:\\", "test");
        BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void JournalBatchFrame_EmptyEntries_RoundTrips()
    {
        var cursor = new UsnJournalCursor(1UL, 0L);
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(buffer, "C:\\", cursor, Array.Empty<UsnJournalEntry>());
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void ReadFrame_UnknownKind_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(buffer); // any no-payload frame gives the right shape
        var bytes = buffer.WrittenSpan.ToArray();
        bytes[4] = 99; // corrupt the kind byte to a value no BrokerFrameKind defines

        Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
    }

    // RequireDrive/RequireMmfName/RequireMessage guard a protocol invariant (these
    // frame kinds are always decoded with a real, non-null string). BrokerFrame is a
    // plain struct, so the violated-invariant case is directly constructible here
    // without needing to fake the wire protocol itself.

    [TestMethod]
    public void RequireDrive_NullDrive_ThrowsInvalidDataException()
    {
        var frame = new BrokerFrame(BrokerFrameKind.Cursor, null, default,
            Array.Empty<UsnJournalEntry>(), null, 0, 0, null, null);

        Assert.ThrowsException<InvalidDataException>(() => frame.RequireDrive());
    }

    [TestMethod]
    public void RequireMmfName_NullMmfName_ThrowsInvalidDataException()
    {
        var frame = new BrokerFrame(BrokerFrameKind.ScanReady, null, default,
            Array.Empty<UsnJournalEntry>(), null, 0, 0, null, null);

        Assert.ThrowsException<InvalidDataException>(() => frame.RequireMmfName());
    }

    [TestMethod]
    public void RequireMessage_NullMessage_ThrowsInvalidDataException()
    {
        var frame = new BrokerFrame(BrokerFrameKind.Error, "C", default,
            Array.Empty<UsnJournalEntry>(), null, 0, 0, null, null);

        Assert.ThrowsException<InvalidDataException>(() => frame.RequireMessage());
    }
}
