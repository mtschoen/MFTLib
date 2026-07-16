using System.Buffers;
using System.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerProtocolTests
{
    static readonly string[] KeepFileNamesGitAndNonAscii = { ".git", "repört" };
    static readonly string[] KeepFileNamesGit = { ".git" };
    static readonly string[] KeepFileNamesSingleLetter = { "a" };

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
        Assert.AreEqual(0, frame.KeepFileNames.Count);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_KeepFileNames()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "C:0:0", KeepFileNamesGitAndNonAscii); // non-ASCII to prove UTF-16
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("C:0:0", frame.DrivesSpec);
        CollectionAssert.AreEqual(KeepFileNamesGitAndNonAscii, (ICollection)frame.KeepFileNames);
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

    // RequireDrive/RequireMmfName/RequireMessage guard a protocol invariant: the
    // Cursor/JournalBatch/Error factories always receive a real, non-null string, so
    // a decoded frame never has these null. default(BrokerFrame) has null string
    // fields, so it exercises the violated-invariant branch without a public path to
    // build an otherwise-invalid frame.

    [TestMethod]
    public void RequireDrive_NullDrive_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireDrive());
    }

    [TestMethod]
    public void RequireMmfName_NullMmfName_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireMmfName());
    }

    [TestMethod]
    public void RequireMessage_NullMessage_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireMessage());
    }

    // Golden wire-byte vectors. These pin the exact serialized bytes of every frame
    // kind so the BrokerFrame construction refactor (positional ctor -> per-kind
    // factories) can be proven byte-identical: the write path is untouched by that
    // refactor, so these must stay green throughout.

    [TestMethod]
    public void WireBytes_Golden_NoPayloadFrames()
    {
        AssertWireBytes(BrokerProtocol.WriteShutdown, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x03 });
        AssertWireBytes(BrokerProtocol.WriteHeartbeat, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x08 });
        AssertWireBytes(BrokerProtocol.WriteEndWatch, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x09 });
        AssertWireBytes(BrokerProtocol.WriteEndWatchAck, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x0A });
    }

    [TestMethod]
    public void WireBytes_Golden_StringFrames()
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, "C"), new byte[]
        {
            0x0B, 0x00, 0x00, 0x00,             // totalLength = 11
            0x01,                               // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drivesSpec "C"
            0x00, 0x00, 0x00, 0x00,             // keepFileNames count = 0
        });
        AssertWireBytes(w => BrokerProtocol.WriteStartWatch(w, "C"),
            new byte[] { 0x07, 0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x00, 0x43, 0x00 });
    }

    [TestMethod]
    public void WireBytes_Golden_ArmAndScanFrame_WithKeepFileNames()
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, "C", KeepFileNamesSingleLetter), new byte[]
        {
            0x11, 0x00, 0x00, 0x00,             // totalLength = 17
            0x01,                               // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drivesSpec "C"
            0x01, 0x00, 0x00, 0x00,             // keepFileNames count = 1
            0x02, 0x00, 0x00, 0x00, 0x61, 0x00, // keepFileNames[0] "a"
        });
    }

    [TestMethod]
    public void WireBytes_Golden_ScanReadyFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteScanReady(w, "m", recordCount: 1, byteLength: 2), new byte[]
        {
            0x17, 0x00, 0x00, 0x00,             // totalLength = 23
            0x04,                               // kind = ScanReady
            0x02, 0x00, 0x00, 0x00, 0x6D, 0x00, // mmfName "m"
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordCount = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // byteLength = 2
        });
    }

    [TestMethod]
    public void WireBytes_Golden_CursorFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteCursor(w, "C", new UsnJournalCursor(1UL, 2L)), new byte[]
        {
            0x17, 0x00, 0x00, 0x00,             // totalLength = 23
            0x05,                               // kind = Cursor
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 2
        });
    }

    [TestMethod]
    public void WireBytes_Golden_ErrorFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteError(w, "C", "D"), new byte[]
        {
            0x0D, 0x00, 0x00, 0x00,             // totalLength = 13
            0x07,                               // kind = Error
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x02, 0x00, 0x00, 0x00, 0x44, 0x00, // message "D"
        });
    }

    [TestMethod]
    public void WireBytes_Golden_JournalBatchFrame_EmptyEntries()
    {
        AssertWireBytes(
            w => BrokerProtocol.WriteJournalBatch(w, "C", new UsnJournalCursor(1UL, 2L), Array.Empty<UsnJournalEntry>()),
            new byte[]
            {
                0x1B, 0x00, 0x00, 0x00,             // totalLength = 27
                0x06,                               // kind = JournalBatch
                0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 2
                0x00, 0x00, 0x00, 0x00,             // entryCount = 0
            });
    }

    static void AssertWireBytes(Action<ArrayBufferWriter<byte>> write, byte[] expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        CollectionAssert.AreEqual(expected, buffer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void Factory_ArmAndScan_PopulatesDrivesSpecAndEmptyEntries()
    {
        var frame = BrokerFrame.ArmAndScan("C:0:0,D:7:42");
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("C:0:0,D:7:42", frame.DrivesSpec);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
        Assert.IsNotNull(frame.Entries);
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.IsNull(frame.Drive);
    }

    [TestMethod]
    public void Factory_ArmAndScan_PopulatesKeepFileNames()
    {
        var frame = BrokerFrame.ArmAndScan("C:0:0", KeepFileNamesGit);
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        CollectionAssert.AreEqual(KeepFileNamesGit, (ICollection)frame.KeepFileNames);
    }

    [TestMethod]
    public void Factory_StartWatch_PopulatesDrivesSpec()
    {
        var frame = BrokerFrame.StartWatch("C:1:100");
        Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);
        Assert.AreEqual("C:1:100", frame.DrivesSpec);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_NoPayloadKinds_SetKindAndEmptyEntries()
    {
        Assert.AreEqual(BrokerFrameKind.Shutdown, BrokerFrame.Shutdown().Kind);
        Assert.AreEqual(BrokerFrameKind.Heartbeat, BrokerFrame.Heartbeat().Kind);
        Assert.AreEqual(BrokerFrameKind.EndWatch, BrokerFrame.EndWatch().Kind);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, BrokerFrame.EndWatchAck().Kind);
        Assert.AreEqual(0, BrokerFrame.Shutdown().Entries.Length);
        Assert.AreEqual(0, BrokerFrame.EndWatchAck().Entries.Length);
    }

    [TestMethod]
    public void Factory_ScanReady_PopulatesMmfHandshake()
    {
        var frame = BrokerFrame.ScanReady("mftlib-scan-1", recordCount: 8_000_000, byteLength: 900_000_000);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frame.Kind);
        Assert.AreEqual("mftlib-scan-1", frame.MmfName);
        Assert.AreEqual(8_000_000, frame.RecordCount);
        Assert.AreEqual(900_000_000L, frame.ByteLength);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_ArmedCursor_PopulatesDriveAndCursor()
    {
        var cursor = new UsnJournalCursor(12345UL, 67890L);
        var frame = BrokerFrame.ArmedCursor("C", cursor);
        Assert.AreEqual(BrokerFrameKind.Cursor, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_JournalBatch_PopulatesDriveCursorEntries()
    {
        var cursor = new UsnJournalCursor(7UL, 110L);
        var entries = new[]
        {
            UsnJournalEntry.Create(1, 5, 10, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "a"),
        };
        var frame = BrokerFrame.JournalBatch("C", cursor, entries);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreSame(entries, frame.Entries);
    }

    [TestMethod]
    public void Factory_Error_PopulatesDriveAndMessage()
    {
        var frame = BrokerFrame.Error("D", "journal wrapped");
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("D", frame.Drive);
        Assert.AreEqual("journal wrapped", frame.Message);
        Assert.AreEqual(0, frame.Entries.Length);
    }
}
