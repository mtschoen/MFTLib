using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The frames of a drive pipe: the two requests (ArmAndScan, StartWatch) and everything the host
// writes back on it. A drive pipe names no drive: the channel is the drive.
public partial class BrokerProtocolTests
{
    static readonly BrokerCatchUpLoss FullLoss = new(
        JournalCheckpointLossCause.CheckpointTrimmed, 1000, 5000, 9000, 4096, 32768, 4000, 12288);

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_SectionAndFullRetention()
    {
        var frame = RoundTrip(BrokerFrame.ArmAndScan("mftlib-scan-C"), BrokerProtocol.WriteArmAndScan);

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("mftlib-scan-C", frame.SectionName);
        Assert.IsNull(frame.RetainedFileNames);
        Assert.IsNull(frame.Drive, "The drive pipe names no drive.");
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ArmAndScanFrame_RoundTrips_IncludeFreed(bool includeFreed)
    {
        var frame = RoundTrip(BrokerFrame.ArmAndScan(
            "s", RetainedFileNamesGitAndNonAscii, includeFreed), BrokerProtocol.WriteArmAndScan);

        Assert.AreEqual(includeFreed, frame.IncludeFreed);
        Assert.AreSequenceEqual(RetainedFileNamesGitAndNonAscii, [.. frame.RetainedFileNames!]);
    }

    [TestMethod]
    public void ReadFrame_ArmAndScan_UnknownIncludeFreedValue_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("s", includeFreed: true));
        var bytes = buffer.WrittenSpan.ToArray();

        // length prefix 4 + kind 1 + section (4 + 2) = the flag's offset.
        bytes[11] = 2;
        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));

        Assert.Contains("2", exception.Message);
    }

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_RetainedFileNames()
    {
        var frame = RoundTrip(BrokerFrame.ArmAndScan(
            "s", RetainedFileNamesGitAndNonAscii), BrokerProtocol.WriteArmAndScan); // non-ASCII to prove UTF-16

        Assert.AreSequenceEqual(RetainedFileNamesGitAndNonAscii, [.. frame.RetainedFileNames!]);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(-99)]
    public void ReadFrame_ArmAndScan_NegativeNameCount_ThrowsInvalidDataException(int negativeCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("s"));
        var bytes = buffer.WrittenSpan.ToArray();
        bytes[12] = 1; // present
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13), negativeCount);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(bytes, out _));

        Assert.Contains(negativeCount.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
    }

    [TestMethod]
    public void StartWatchFrame_RoundTrips_Cursor()
    {
        var cursor = new UsnJournalCursor(1UL, 100L);
        var frame = RoundTrip(BrokerFrame.StartWatch(cursor), BrokerProtocol.WriteStartWatch);

        Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void CursorFrame_RoundTrips_Cursor()
    {
        var cursor = new UsnJournalCursor(12345UL, 67890L);
        var frame = RoundTrip(BrokerFrame.ArmedCursor(cursor), BrokerProtocol.WriteCursor);

        Assert.AreEqual(BrokerFrameKind.Cursor, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.IsNull(frame.Drive);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void ScanProgressFrame_RoundTrips_AllFields()
    {
        var progress = new BrokerScanProgress
        {
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 1000,
            BytesProcessed = 20480,
            TotalRecords = 5000,
            TotalBytes = 102400
        };
        var frame = RoundTrip(BrokerFrame.ScanProgress(progress), BrokerProtocol.WriteScanProgress);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.IsNotNull(frame.Progress);
        Assert.AreEqual(progress, frame.Progress.Value);
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress.Value.Phase);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(0L, 0L)]
    [DataRow(-1L, -1L)]
    [DataRow(500L, null)]
    [DataRow(null, 1024L)]
    [DataRow(-1L, 0L)]
    public void ScanProgressFrame_RoundTrips_NullableFields(long? totalRecords, long? totalBytes)
    {
        var progress = new BrokerScanProgress
        {
            Phase = BrokerScanPhase.Transferring,
            RecordsProcessed = 500,
            BytesProcessed = 10240,
            TotalRecords = totalRecords,
            TotalBytes = totalBytes
        };
        var frame = RoundTrip(BrokerFrame.ScanProgress(progress), BrokerProtocol.WriteScanProgress);

        Assert.AreEqual(BrokerScanPhase.Transferring, frame.Progress!.Value.Phase);
        Assert.AreEqual(totalRecords, frame.Progress.Value.TotalRecords);
        Assert.AreEqual(totalBytes, frame.Progress.Value.TotalBytes);
    }

    [TestMethod]
    [DataRow(99, DisplayName = "an undefined phase")]
    [DataRow(256, DisplayName = "a phase outside the byte range")]
    public void ReadFrame_ScanProgress_InvalidPhase_ThrowsInvalidDataException(int phase)
    {
        byte[] payload =
        [
            0x27, 0x00, 0x00, 0x00, // totalLength = 39
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase, written below
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 2
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // totalRecords = absent
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // totalBytes = absent
        ];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5), phase);

        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(payload, out _));
    }

    [TestMethod]
    public void CatchUpLostFrame_RoundTrips_EveryLossField()
    {
        var frame = RoundTrip(BrokerFrame.CatchUpLost(FullLoss), BrokerProtocol.WriteCatchUpLost);

        Assert.AreEqual(BrokerFrameKind.CatchUpLost, frame.Kind);
        Assert.IsNull(frame.Message);
        var loss = frame.RequireCatchUpLoss('C');
        Assert.AreEqual('C', loss.Drive);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(1000L, loss.CheckpointUsn);
        Assert.AreEqual(5000L, loss.FirstUsn);
        Assert.AreEqual(9000L, loss.NextUsn);
        Assert.AreEqual(4096L, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(32768L, loss.JournalSettings.MaximumSize);
        Assert.AreEqual(4000L, loss.BytesBehind);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public void CatchUpLostFrame_RoundTrips_NullBytesBehindAndSizeThatWouldHaveRetained()
    {
        var recreated = FullLoss with
        {
            Cause = JournalCheckpointLossCause.JournalRecreated,
            BytesBehind = null,
            SizeThatWouldHaveRetained = null
        };
        var frame = RoundTrip(BrokerFrame.CatchUpLost(recreated), BrokerProtocol.WriteCatchUpLost);

        var loss = frame.RequireCatchUpLoss('D');
        Assert.AreEqual('D', loss.Drive, "The drive is the channel's, not the wire's.");
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
        Assert.AreEqual(9000L, loss.NextUsn);
    }

    [TestMethod]
    public void CatchUpLostFrame_RoundTrips_NegativeBytesBehind()
    {
        var frame = RoundTrip(BrokerFrame.CatchUpLost(FullLoss with { BytesBehind = -1 }), BrokerProtocol.WriteCatchUpLost);

        Assert.AreEqual(-1L, frame.RequireCatchUpLoss('C').BytesBehind, "A present value of -1 is not the absent marker.");
    }

    [TestMethod]
    public void ReadFrame_CatchUpLost_UnknownCause_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCatchUpLost(buffer, BrokerFrame.CatchUpLost(FullLoss with { Cause = (JournalCheckpointLossCause)42 }));

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));
        Assert.AreEqual("Unknown checkpoint loss cause: 42", exception.Message);
    }

    [TestMethod]
    public void ScanReadyFrame_RoundTrips_SkippedRecordCount()
    {
        var frame = RoundTrip(BrokerFrame.ScanReady(3), BrokerProtocol.WriteScanReady);

        Assert.AreEqual(BrokerFrameKind.ScanReady, frame.Kind);
        Assert.AreEqual(3L, frame.SkippedRecordCount);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void ScanCompletedFrame_RoundTrips_NoPayload()
    {
        var frame = RoundTrip(BrokerFrame.ScanCompleted(), BrokerProtocol.WriteScanCompleted);

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frame.Kind);
        Assert.AreEqual(default(UsnJournalCursor), frame.Cursor);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void JournalBatchFrame_RoundTrips_EntriesAndCursor()
    {
        var entries = new[]
        {
            JournalEntries.Create(1, 10, "a"),
            JournalEntries.Create(2, 20, "b", UsnReason.FileDelete | UsnReason.Close)
        };
        var cursor = new UsnJournalCursor(99UL, 20L);

        var frame = RoundTrip(BrokerFrame.JournalBatch(cursor, entries), BrokerProtocol.WriteJournalBatch);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.HasCount(2, frame.Entries);
        Assert.AreEqual("b", frame.Entries[1].FileName);
        Assert.IsNull(frame.Drive);
    }

    [TestMethod]
    public void JournalBatchFrame_EmptyEntries_RoundTrips()
    {
        var frame = RoundTrip(BrokerFrame.JournalBatch(
            new UsnJournalCursor(1UL, 0L), []), BrokerProtocol.WriteJournalBatch);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void CaughtUpFrame_RoundTrips_NoPayload()
    {
        var frame = RoundTrip(BrokerFrame.CaughtUp(), BrokerProtocol.WriteCaughtUp);

        Assert.AreEqual(BrokerFrameKind.CaughtUp, frame.Kind);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    [DataRow(false, (byte)0)]
    [DataRow(true, (byte)1)]
    public void WireBytes_Golden_ArmAndScanFrame(bool includeFreed, byte expectedFlagByte)
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, BrokerFrame.ArmAndScan("C", includeFreed: includeFreed)),
        [
            0x0D, 0x00, 0x00, 0x00, // totalLength = 13
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            expectedFlagByte, // includeFreed
            0x00, 0x00, 0x00, 0x00, 0x00 // retainedFileNames = absent
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ArmAndScanFrame_WithRetainedFileNames()
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, BrokerFrame.ArmAndScan("C",
                RetainedFileNamesSingleLetter)),
        [
            0x13, 0x00, 0x00, 0x00, // totalLength = 19
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            0x00, // includeFreed = false
            0x01, 0x01, 0x00, 0x00, 0x00, // retainedFileNames count = present, 1
            0x02, 0x00, 0x00, 0x00, 0x61, 0x00 // retainedFileNames[0] "a"
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_CursorFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteCursor(w, BrokerFrame.ArmedCursor(new UsnJournalCursor(1UL, 2L))),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x0B, // kind = Cursor
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalIdentifier = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // nextUsn = 2
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanProgressFrame()
    {
        var progress = new BrokerScanProgress
        {
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 100,
            BytesProcessed = 200,
            TotalRecords = 300,
            TotalBytes = 400
        };
        AssertWireBytes(w => BrokerProtocol.WriteScanProgress(w, BrokerFrame.ScanProgress(progress)),
        [
            0x27, 0x00, 0x00, 0x00, // totalLength = 39
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase = Parsing (0)
            0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 100
            0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 200
            0x01, 0x2C, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // totalRecords = present, 300
            0x01, 0x90, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // totalBytes = present, 400
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanProgressFrame_NullTotals()
    {
        var progress = new BrokerScanProgress
        {
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 1,
            BytesProcessed = 2,
            TotalRecords = null,
            TotalBytes = null
        };
        AssertWireBytes(w => BrokerProtocol.WriteScanProgress(w, BrokerFrame.ScanProgress(progress)),
        [
            0x27, 0x00, 0x00, 0x00, // totalLength = 39
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase = Parsing (0)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 2
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // totalRecords = absent
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // totalBytes = absent
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanReadyFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteScanReady(w, BrokerFrame.ScanReady(3)),
        [
            0x09, 0x00, 0x00, 0x00, // totalLength = 9
            0x0E, // kind = ScanReady
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // skippedRecordCount = 3
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanCompletedFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteScanCompleted(w, BrokerFrame.ScanCompleted()),
        [
            0x01, 0x00, 0x00, 0x00, // totalLength = 1
            0x12 // kind = ScanCompleted
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_CatchUpLostFrame()
    {
        var loss = FullLoss with
        {
            CheckpointUsn = 1,
            FirstUsn = 2,
            NextUsn = 3,
            AllocationDelta = 4,
            MaximumSize = 5,
            BytesBehind = 6,
            SizeThatWouldHaveRetained = null
        };
        AssertWireBytes(w => BrokerProtocol.WriteCatchUpLost(w, BrokerFrame.CatchUpLost(loss)),
        [
            0x3F, 0x00, 0x00, 0x00, // totalLength = 63
            0x0D, // kind = CatchUpLost
            0x00, 0x00, 0x00, 0x00, // cause = CheckpointTrimmed (0)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // checkpointUsn = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // firstUsn = 2
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 3
            0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // allocationDelta = 4
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // maximumSize = 5
            0x01, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesBehind = present, 6
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // sizeThatWouldHaveRetained = absent
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_JournalBatchFrame()
    {
        AssertWireBytes(
            w => BrokerProtocol.WriteJournalBatch(w, BrokerFrame.JournalBatch(new UsnJournalCursor(1UL, 2L), [])),
            [
                0x15, 0x00, 0x00, 0x00, // totalLength = 21
                0x0F, // kind = JournalBatch
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalIdentifier = 1
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 2
                0x00, 0x00, 0x00, 0x00 // entryCount = 0
            ]);
    }

    [TestMethod]
    public void WireBytes_Golden_StartWatchFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteStartWatch(w, BrokerFrame.StartWatch(new UsnJournalCursor(1UL, 2L))),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x10, // kind = StartWatch
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalIdentifier = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // nextUsn = 2
        ]);
    }

    [TestMethod]
    public void Factory_ArmAndScan_WithoutRetainedNames_HasNullList()
    {
        var frame = BrokerFrame.ArmAndScan("section");

        Assert.IsNull(frame.RetainedFileNames);
    }

    [TestMethod]
    public void Factory_JournalBatch_PopulatesCursorAndEntries()
    {
        var cursor = new UsnJournalCursor(7UL, 110L);
        var entries = new[] { JournalEntries.Create(1, 10, "a") };

        var frame = BrokerFrame.JournalBatch(cursor, entries);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreSame(entries, frame.Entries);
    }

    [TestMethod]
    public void Factory_ScanProgress_PopulatesProgress()
    {
        var progress = new BrokerScanProgress
        {
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 10,
            BytesProcessed = 20,
            TotalRecords = 30,
            TotalBytes = 40
        };

        var frame = BrokerFrame.ScanProgress(progress);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual(progress, frame.Progress);
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress!.Value.Phase);
        Assert.IsEmpty(frame.Entries);
    }
}
