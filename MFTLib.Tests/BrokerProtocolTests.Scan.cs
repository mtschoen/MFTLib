using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The frames of a drive pipe: the two requests (ArmAndScan, StartWatch) and everything the host
// writes back on it. A drive pipe names no drive: the channel is the drive.
public partial class BrokerProtocolTests
{
    static readonly JournalCheckpointLoss FullLoss = new JournalCheckpointLoss('C', JournalCheckpointLossDetection.ScanCatchUp, JournalCheckpointLossCause.CheckpointTrimmed,
        new UsnJournalSettings { AllocationDelta = 4096, MaximumSize = 32768 })
    {
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_SectionAndFullRetention()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteArmAndScan(writer, "mftlib-scan-C"));

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("mftlib-scan-C", frame.SectionName);
        Assert.IsNull(frame.DirectoryScanFileNames);
        Assert.IsNull(frame.Drive, "The drive pipe names no drive.");
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ArmAndScanFrame_RoundTrips_IncludeFreed(bool includeFreed)
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteArmAndScan(
            writer, "s", KeepFileNamesGitAndNonAscii, includeFreed));

        Assert.AreEqual(includeFreed, frame.IncludeFreed);
        CollectionAssert.AreEqual(KeepFileNamesGitAndNonAscii, frame.DirectoryScanFileNames!.ToArray());
    }

    [TestMethod]
    public void ReadFrame_ArmAndScan_UnknownIncludeFreedValue_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "s", includeFreed: true);
        var bytes = buffer.WrittenSpan.ToArray();

        // length prefix 4 + kind 1 + section (4 + 2) = the flag's offset.
        bytes[11] = 2;
        var exception = Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));

        StringAssert.Contains(exception.Message, "2");
    }

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_KeepFileNames()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteArmAndScan(
            writer, "s", KeepFileNamesGitAndNonAscii)); // non-ASCII to prove UTF-16

        CollectionAssert.AreEqual(KeepFileNamesGitAndNonAscii, frame.DirectoryScanFileNames!.ToArray());
    }

    [TestMethod]
    public void ReadFrame_ArmAndScan_NegativeNameCountBelowNull_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "s");
        var bytes = buffer.WrittenSpan.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15), -99);

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(bytes, out _));

        StringAssert.Contains(exception.Message, "99");
    }

    [TestMethod]
    public void StartWatchFrame_RoundTrips_Cursor()
    {
        var cursor = new UsnJournalCursor(1UL, 100L);
        var frame = RoundTrip(writer => BrokerProtocol.WriteStartWatch(writer, cursor));

        Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void CursorFrame_RoundTrips_Cursor()
    {
        var cursor = new UsnJournalCursor(12345UL, 67890L);
        var frame = RoundTrip(writer => BrokerProtocol.WriteCursor(writer, cursor));

        Assert.AreEqual(BrokerFrameKind.Cursor, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.IsNull(frame.Drive);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void ScanProgressFrame_RoundTrips_AllFieldsWithEmptyDrive()
    {
        var progress = new BrokerScanProgress
        {
            DriveLetter = "C",
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 1000,
            BytesProcessed = 20480,
            TotalRecords = 5000,
            TotalBytes = 102400,
            Elapsed = TimeSpan.FromMilliseconds(150)
        };
        var frame = RoundTrip(writer => BrokerProtocol.WriteScanProgress(writer, progress));

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.IsNotNull(frame.Progress);
        Assert.AreEqual(progress with { DriveLetter = string.Empty }, frame.Progress.Value,
            "The wire carries no drive: the client fills it in from the channel.");
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress.Value.Phase);
    }

    [TestMethod]
    public void ScanProgressFrame_RoundTrips_NullableFieldsAsMinusOne()
    {
        var progress = new BrokerScanProgress
        {
            DriveLetter = "D",
            Phase = BrokerScanPhase.Transferring,
            RecordsProcessed = 500,
            BytesProcessed = 10240,
            TotalRecords = null,
            TotalBytes = null,
            Elapsed = TimeSpan.FromMilliseconds(50)
        };
        var frame = RoundTrip(writer => BrokerProtocol.WriteScanProgress(writer, progress));

        Assert.AreEqual(BrokerScanPhase.Transferring, frame.Progress!.Value.Phase);
        Assert.IsNull(frame.Progress.Value.TotalRecords);
        Assert.IsNull(frame.Progress.Value.TotalBytes);
    }

    [DataTestMethod]
    [DataRow(99, DisplayName = "an undefined phase")]
    [DataRow(256, DisplayName = "a phase outside the byte range")]
    public void ReadFrame_ScanProgress_InvalidPhase_ThrowsInvalidDataException(int phase)
    {
        byte[] payload =
        [
            0x2D, 0x00, 0x00, 0x00, // totalLength = 45
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase, written below
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 2
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalRecords = -1
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalBytes = -1
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // elapsedTicks = 3
        ];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5), phase);

        Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(payload, out _));
    }

    [TestMethod]
    public void CatchUpLostFrame_RoundTrips_EveryLossField()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteCatchUpLost(writer, FullLoss));

        Assert.AreEqual(BrokerFrameKind.CatchUpLost, frame.Kind);
        Assert.IsNull(frame.Message);
        var loss = frame.RequireCatchUpLoss('C');
        Assert.AreEqual('C', loss.DriveLetter);
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
        var frame = RoundTrip(writer => BrokerProtocol.WriteCatchUpLost(writer, recreated));

        var loss = frame.RequireCatchUpLoss('D');
        Assert.AreEqual('D', loss.DriveLetter, "The drive is the channel's, not the wire's.");
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
        Assert.AreEqual(9000L, loss.NextUsn);
    }

    [TestMethod]
    public void CatchUpLostFrame_RoundTrips_NegativeBytesBehind()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteCatchUpLost(writer, FullLoss with { BytesBehind = -1 }));

        Assert.AreEqual(-1L, frame.RequireCatchUpLoss('C').BytesBehind, "A present value of -1 is not the absent marker.");
    }

    [TestMethod]
    public void ReadFrame_CatchUpLost_UnknownCause_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCatchUpLost(buffer, FullLoss with { Cause = (JournalCheckpointLossCause)42 });

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));
        Assert.AreEqual("Unknown checkpoint loss cause: 42", exception.Message);
    }

    [TestMethod]
    public void ScanReadyFrame_RoundTrips_SkippedRecordCount()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteScanReady(writer, 3));

        Assert.AreEqual(BrokerFrameKind.ScanReady, frame.Kind);
        Assert.AreEqual(3L, frame.SkippedRecordCount);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void ScanCompletedFrame_RoundTrips_AdvancedCursorAndCarriesNoEntries()
    {
        var cursor = new UsnJournalCursor(7UL, 1500L);
        var frame = RoundTrip(writer => BrokerProtocol.WriteScanCompleted(writer, cursor));

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(0, frame.Entries.Length);
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

        var frame = RoundTrip(writer => BrokerProtocol.WriteJournalBatch(writer, cursor, entries));

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(2, frame.Entries.Length);
        Assert.AreEqual("b", frame.Entries[1].FileName);
        Assert.IsNull(frame.Drive);
    }

    [TestMethod]
    public void JournalBatchFrame_EmptyEntries_RoundTrips()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteJournalBatch(
            writer, new UsnJournalCursor(1UL, 0L), Array.Empty<UsnJournalEntry>()));

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void CaughtUpFrame_RoundTrips_NoPayload()
    {
        var frame = RoundTrip(BrokerProtocol.WriteCaughtUp);

        Assert.AreEqual(BrokerFrameKind.CaughtUp, frame.Kind);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [DataTestMethod]
    [DataRow(false, (byte)0)]
    [DataRow(true, (byte)1)]
    public void WireBytes_Golden_ArmAndScanFrame(bool includeFreed, byte expectedFlagByte)
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, "C", includeFreed: includeFreed),
        [
            0x0F, 0x00, 0x00, 0x00, // totalLength = 15
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            expectedFlagByte, 0x00, 0x00, 0x00, // includeFreed
            0xFF, 0xFF, 0xFF, 0xFF // name count = -1 (full scan)
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ArmAndScanFrame_WithKeepFileNames()
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, "C",
                KeepFileNamesSingleLetter),
        [
            0x15, 0x00, 0x00, 0x00, // totalLength = 21
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            0x00, 0x00, 0x00, 0x00, // includeFreed = false
            0x01, 0x00, 0x00, 0x00, // keepFileNames count = 1
            0x02, 0x00, 0x00, 0x00, 0x61, 0x00 // keepFileNames[0] "a"
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_CursorFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteCursor(w, new UsnJournalCursor(1UL, 2L)),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x0B, // kind = Cursor
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // nextUsn = 2
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanProgressFrame()
    {
        var progress = new BrokerScanProgress
        {
            DriveLetter = "C",
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 100,
            BytesProcessed = 200,
            TotalRecords = 300,
            TotalBytes = 400,
            Elapsed = TimeSpan.FromTicks(500)
        };
        AssertWireBytes(w => BrokerProtocol.WriteScanProgress(w, progress),
        [
            0x2D, 0x00, 0x00, 0x00, // totalLength = 45 (1 + 4 + 8*5)
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase = Parsing (0)
            0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 100
            0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 200
            0x2C, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // totalRecords = 300
            0x90, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // totalBytes = 400
            0xF4, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // elapsedTicks = 500
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanProgressFrame_NullTotals()
    {
        var progress = new BrokerScanProgress
        {
            DriveLetter = "C",
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 1,
            BytesProcessed = 2,
            TotalRecords = null,
            TotalBytes = null,
            Elapsed = TimeSpan.FromTicks(3)
        };
        AssertWireBytes(w => BrokerProtocol.WriteScanProgress(w, progress),
        [
            0x2D, 0x00, 0x00, 0x00, // totalLength = 45
            0x0C, // kind = ScanProgress
            0x00, 0x00, 0x00, 0x00, // phase = Parsing (0)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 2
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalRecords = -1
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalBytes = -1
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // elapsedTicks = 3
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanReadyFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteScanReady(w, 3),
        [
            0x09, 0x00, 0x00, 0x00, // totalLength = 9
            0x0E, // kind = ScanReady
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // skippedRecordCount = 3
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ScanCompletedFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteScanCompleted(w, new UsnJournalCursor(1UL, 2L)),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x12, // kind = ScanCompleted
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // nextUsn = 2
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
            JournalSettings = new UsnJournalSettings { AllocationDelta = 4, MaximumSize = 5 },
            BytesBehind = 6,
            SizeThatWouldHaveRetained = null
        };
        AssertWireBytes(w => BrokerProtocol.WriteCatchUpLost(w, loss),
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
            w => BrokerProtocol.WriteJournalBatch(w, new UsnJournalCursor(1UL, 2L), Array.Empty<UsnJournalEntry>()),
            [
                0x15, 0x00, 0x00, 0x00, // totalLength = 21
                0x0F, // kind = JournalBatch
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 2
                0x00, 0x00, 0x00, 0x00 // entryCount = 0
            ]);
    }

    [TestMethod]
    public void WireBytes_Golden_StartWatchFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteStartWatch(w, new UsnJournalCursor(1UL, 2L)),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x10, // kind = StartWatch
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // nextUsn = 2
        ]);
    }

    [TestMethod]
    public void Factory_ArmAndScan_WithoutRetainedNames_HasNullList()
    {
        var frame = BrokerFrame.ArmAndScan("section");

        Assert.IsNull(frame.DirectoryScanFileNames);
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
            DriveLetter = "E",
            Phase = BrokerScanPhase.Parsing,
            RecordsProcessed = 10,
            BytesProcessed = 20,
            TotalRecords = 30,
            TotalBytes = 40,
            Elapsed = TimeSpan.FromSeconds(1)
        };

        var frame = BrokerFrame.ScanProgress(progress);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual(progress, frame.Progress);
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress!.Value.Phase);
        Assert.AreEqual(0, frame.Entries.Length);
    }
}
