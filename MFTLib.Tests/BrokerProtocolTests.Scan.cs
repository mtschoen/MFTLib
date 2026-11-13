using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The frames of a volume pipe: the two requests (ArmAndScan, StartWatch) and everything the host
// writes back on it. A volume pipe names no volume: the channel is the volume.
public partial class BrokerProtocolTests
{
    static readonly BrokerCatchUpLoss FullLoss = new(
        JournalCheckpointLossCause.CheckpointTrimmed, 1000, 5000, 9000, 4096, 32768, 4000, 12288);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void ArmAndScan_RoundTripsSectionAndFlags(bool includeFreed, bool deleteOnClose)
    {
        var frame = RoundTrip(BrokerFrame.ArmAndScan("mftlib-scan-C", includeFreed, deleteOnClose), BrokerProtocol.WriteArmAndScan);

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("mftlib-scan-C", frame.SectionName);
        Assert.AreEqual(includeFreed, frame.IncludeFreed);
        Assert.AreEqual(deleteOnClose, frame.DeleteOnClose);
        Assert.IsNull(frame.Volume, "The volume pipe names no volume.");
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void ReadFrame_ArmAndScan_UnknownIncludeFreedValue_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("s", includeFreed: true, deleteOnClose: false));
        var bytes = buffer.WrittenSpan.ToArray();

        // length prefix 4 + kind 1 + section (4 + 2) = the flag's offset.
        bytes[11] = 2;
        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));

        Assert.Contains("2", exception.Message);
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
        Assert.IsNull(frame.Volume);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void ScanProgressFrame_RoundTrips_AllFields()
    {
        var progress = BlockWriteProgress.Scanning(1000, 5000);
        var frame = RoundTrip(BrokerFrame.ScanProgress(progress), BrokerProtocol.WriteScanProgress);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.IsNotNull(frame.Progress);
        Assert.AreEqual(progress, frame.Progress.Value);
        Assert.AreEqual(BlockScanPhase.Scanning, frame.Progress.Value.Phase);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(500L)]
    public void ScanProgressFrame_RoundTrips_NullableTotal(long? totalRecords)
    {
        var progress = BlockWriteProgress.Scanning(500, totalRecords);
        var frame = RoundTrip(BrokerFrame.ScanProgress(progress), BrokerProtocol.WriteScanProgress);

        Assert.AreEqual(BlockScanPhase.Scanning, frame.Progress!.Value.Phase);
        Assert.AreEqual(totalRecords, frame.Progress.Value.TotalRecords);
        Assert.AreEqual(500L, frame.Progress.Value.RecordsProcessed);
    }

    [TestMethod]
    public void ScanProgressFrame_RoundTrips_IndexingSample()
    {
        var frame = RoundTrip(BrokerFrame.ScanProgress(BlockWriteProgress.Indexing), BrokerProtocol.WriteScanProgress);

        Assert.AreEqual(BlockWriteProgress.Indexing, frame.Progress!.Value);
    }

    [TestMethod]
    [DataRow(99, DisplayName = "an undefined phase")]
    [DataRow(256, DisplayName = "a phase outside the byte range")]
    public void ReadFrame_ScanProgress_InvalidPhase_ThrowsInvalidDataException(int phase)
    {
        byte[] payload =
        [
            0x16, 0x00, 0x00, 0x00, // totalLength = 22
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase, written below
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // totalRecords = absent
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
        Assert.AreEqual('C', loss.Volume);
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
        Assert.AreEqual('D', loss.Volume, "The volume is the channel's, not the wire's.");
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
        Assert.IsNull(frame.Volume);
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
    [DataRow(false, false, (byte)0, (byte)0)]
    [DataRow(true, false, (byte)1, (byte)0)]
    [DataRow(false, true, (byte)0, (byte)1)]
    public void WireBytes_Golden_ArmAndScanFrame(bool includeFreed, bool deleteOnClose, byte expectedFreedByte, byte expectedDeleteByte)
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, BrokerFrame.ArmAndScan("C", includeFreed, deleteOnClose)),
        [
            0x09, 0x00, 0x00, 0x00, // totalLength = 9
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            expectedFreedByte, // includeFreed
            expectedDeleteByte // deleteOnClose
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
        AssertWireBytes(w => BrokerProtocol.WriteScanProgress(w, BrokerFrame.ScanProgress(
                BlockWriteProgress.Scanning(100, 300))),
        [
            0x16, 0x00, 0x00, 0x00, // totalLength = 22
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase = Scanning (0)
            0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 100
            0x01, 0x2C, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // totalRecords = present, 300
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanProgressFrame_IndexingSample()
    {
        AssertWireBytes(w => BrokerProtocol.WriteScanProgress(w, BrokerFrame.ScanProgress(
                BlockWriteProgress.Indexing)),
        [
            0x16, 0x00, 0x00, 0x00, // totalLength = 22
            0x0C, // kind = ScanProgress
            0x01, 0x00, 0x00, 0x00, // phase = Indexing (1)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 0
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // totalRecords = absent
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
        var progress = BlockWriteProgress.Scanning(10, 30);

        var frame = BrokerFrame.ScanProgress(progress);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual(progress, frame.Progress);
        Assert.AreEqual(BlockScanPhase.Scanning, frame.Progress!.Value.Phase);
        Assert.IsEmpty(frame.Entries);
    }
}
