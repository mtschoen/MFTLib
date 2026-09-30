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
    static readonly JournalCheckpointLoss FullLoss = new()
    {
        DriveLetter = 'C',
        DetectedDuring = JournalCheckpointLossDetection.ScanCatchUp,
        Cause = JournalCheckpointLossCause.CheckpointTrimmed,
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        AllocationDelta = 4096,
        MaximumSize = 32768,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_SectionAndProfile()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteArmAndScan(writer, "mftlib-scan-C", BrokerScanProfile.Full));

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("mftlib-scan-C", frame.SectionName);
        Assert.AreEqual(BrokerScanProfile.Full, frame.Profile);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
        Assert.IsNull(frame.Drive, "The drive pipe names no drive.");
    }

    [TestMethod]
    public void ArmAndScanFrame_RoundTrips_KeepFileNames()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteArmAndScan(
            writer, "s", BrokerScanProfile.DirectoryIndex, KeepFileNamesGitAndNonAscii)); // non-ASCII to prove UTF-16

        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, frame.Profile);
        CollectionAssert.AreEqual(KeepFileNamesGitAndNonAscii, frame.KeepFileNames.ToArray());
    }

    [TestMethod]
    public void ReadFrame_ArmAndScan_UnknownProfile_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "s", (BrokerScanProfile)99);

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));

        StringAssert.Contains(exception.Message, "99");
    }

    [TestMethod]
    public void StartWatchFrame_RoundTrips_Cursor()
    {
        var cursor = new UsnJournalCursor(1UL, 100L);
        var frame = RoundTrip(writer => BrokerProtocol.WriteStartWatch(writer, cursor));

        Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
    }

    [TestMethod]
    public void CursorFrame_RoundTrips_Cursor()
    {
        var cursor = new UsnJournalCursor(12345UL, 67890L);
        var frame = RoundTrip(writer => BrokerProtocol.WriteCursor(writer, cursor));

        Assert.AreEqual(BrokerFrameKind.Cursor, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.IsNull(frame.Drive);
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
    public void CatchUpLostFrame_RoundTrips_EveryLossFieldAndMessage()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteCatchUpLost(writer, FullLoss, "catch-up read failed"));

        Assert.AreEqual(BrokerFrameKind.CatchUpLost, frame.Kind);
        Assert.AreEqual("catch-up read failed", frame.Message);
        var loss = frame.RequireCatchUpLoss('C');
        Assert.AreEqual('C', loss.DriveLetter);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(1000L, loss.CheckpointUsn);
        Assert.AreEqual(5000L, loss.FirstUsn);
        Assert.AreEqual(9000L, loss.NextUsn);
        Assert.AreEqual(4096L, loss.AllocationDelta);
        Assert.AreEqual(32768L, loss.MaximumSize);
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
        var frame = RoundTrip(writer => BrokerProtocol.WriteCatchUpLost(writer, recreated, "recreated"));

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
        var frame = RoundTrip(writer => BrokerProtocol.WriteCatchUpLost(writer, FullLoss with { BytesBehind = -1 }, "m"));

        Assert.AreEqual(-1L, frame.RequireCatchUpLoss('C').BytesBehind, "A present value of -1 is not the absent marker.");
    }

    [TestMethod]
    public void ReadFrame_CatchUpLost_UnknownCause_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCatchUpLost(buffer, FullLoss with { Cause = (JournalCheckpointLossCause)42 }, "m");

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));
        Assert.AreEqual("Unknown checkpoint loss cause: 42", exception.Message);
    }

    [TestMethod]
    public void ScanReadyFrame_RoundTrips_Counts()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteScanReady(writer, 8_000_000, 900_000_000, 3));

        Assert.AreEqual(BrokerFrameKind.ScanReady, frame.Kind);
        Assert.AreEqual(8_000_000L, frame.RowCount);
        Assert.AreEqual(900_000_000L, frame.NamePoolUsedBytes);
        Assert.AreEqual(3L, frame.SkippedRecordCount);
    }

    [TestMethod]
    public void JournalBatchFrame_RoundTrips_EntriesAndCursor()
    {
        var entries = new[]
        {
            JournalEntryFactory.Create(1, 10, "a"),
            JournalEntryFactory.Create(2, 20, "b", UsnReason.FileDelete | UsnReason.Close)
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
    }

    [TestMethod]
    public void WireBytes_Golden_ArmAndScanFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, "C", BrokerScanProfile.Full),
        [
            0x0F, 0x00, 0x00, 0x00, // totalLength = 15
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            0x00, 0x00, 0x00, 0x00, // profile = Full
            0x00, 0x00, 0x00, 0x00 // keepFileNames count = 0
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ArmAndScanFrame_WithKeepFileNames()
    {
        AssertWireBytes(w => BrokerProtocol.WriteArmAndScan(w, "C", BrokerScanProfile.DirectoryIndex,
                KeepFileNamesSingleLetter),
        [
            0x15, 0x00, 0x00, 0x00, // totalLength = 21
            0x0A, // kind = ArmAndScan
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // sectionName "C"
            0x01, 0x00, 0x00, 0x00, // profile = DirectoryIndex
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
        AssertWireBytes(w => BrokerProtocol.WriteScanReady(w, 1, 2, 3),
        [
            0x19, 0x00, 0x00, 0x00, // totalLength = 25
            0x0E, // kind = ScanReady
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // rowCount = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // namePoolUsedBytes = 2
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // skippedRecordCount = 3
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
        AssertWireBytes(w => BrokerProtocol.WriteCatchUpLost(w, loss, "m"),
        [
            0x45, 0x00, 0x00, 0x00, // totalLength = 69
            0x0D, // kind = CatchUpLost
            0x00, 0x00, 0x00, 0x00, // cause = CheckpointTrimmed (0)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // checkpointUsn = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // firstUsn = 2
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 3
            0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // allocationDelta = 4
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // maximumSize = 5
            0x01, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesBehind = present, 6
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // sizeThatWouldHaveRetained = absent
            0x02, 0x00, 0x00, 0x00, 0x6D, 0x00 // message "m"
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
    public void Factory_ArmAndScan_PopulatesSectionProfileAndKeepFileNames()
    {
        var frame = BrokerFrame.ArmAndScan("section", BrokerScanProfile.DirectoryIndex, KeepFileNamesGit);

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("section", frame.SectionName);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, frame.Profile);
        CollectionAssert.AreEqual(KeepFileNamesGit, frame.KeepFileNames.ToArray());
        Assert.IsNotNull(frame.Entries);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_ArmAndScan_WithoutKeepFileNames_HasEmptyList()
    {
        var frame = BrokerFrame.ArmAndScan("section", BrokerScanProfile.Full);

        Assert.AreEqual(0, frame.KeepFileNames.Count);
    }

    [TestMethod]
    public void Factory_JournalBatch_PopulatesCursorAndEntries()
    {
        var cursor = new UsnJournalCursor(7UL, 110L);
        var entries = new[] { JournalEntryFactory.Create(1, 10, "a") };

        var frame = BrokerFrame.JournalBatch(cursor, entries);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreSame(entries, frame.Entries);
    }

    [TestMethod]
    public void Factory_ScanReady_PopulatesCounts()
    {
        var frame = BrokerFrame.ScanReady(8_000_000, 900_000_000, 0);

        Assert.AreEqual(BrokerFrameKind.ScanReady, frame.Kind);
        Assert.AreEqual(8_000_000L, frame.RowCount);
        Assert.AreEqual(900_000_000L, frame.NamePoolUsedBytes);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_ScanProgress_PopulatesProgress()
    {
        var progress = new BrokerScanProgress("E", 10, 20, 30, 40, TimeSpan.FromSeconds(1));

        var frame = BrokerFrame.ScanProgress(progress);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual(progress, frame.Progress);
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress!.Value.Phase);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_ArmedCursor_PopulatesCursor()
    {
        var cursor = new UsnJournalCursor(12345UL, 67890L);

        var frame = BrokerFrame.ArmedCursor(cursor);

        Assert.AreEqual(BrokerFrameKind.Cursor, frame.Kind);
        Assert.AreEqual(cursor, frame.Cursor);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_StartWatch_PopulatesCursor()
    {
        var frame = BrokerFrame.StartWatch(new UsnJournalCursor(1UL, 100L));

        Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);
        Assert.AreEqual(new UsnJournalCursor(1UL, 100L), frame.Cursor);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    // The base-commit client test asserted that the old client wrote one request; with the
    // client gone the wire contract is pinned on the host side: an ArmAndScan written by
    // BrokerProtocol reaches the section writer with exactly the section, profile and names sent.
    [TestMethod]
    public async Task ArmAndScan_RawFrameOnDrivePipe_ReachesSectionWriterWithSectionProfileAndKeepNames()
    {
        using var sectionWriter = new RecordingBlockSectionWriter();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7, 1000),
            (_, _, _, _, _) => [[new MftRecord(5, 5, new MftRecordFields(3), ".", null)]],
            (_, since, _) => (Array.Empty<UsnJournalEntry>(), since),
            processorCount: 2);
        await using var harness = new HostChannelHarness(host, sectionWriter);

        var pipe = await harness.OpenScanChannelAsync('D', "section-D", BrokerScanProfile.DirectoryIndex, KeepFileNamesGit);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind);
        Assert.AreEqual("section-D", sectionWriter.LastSectionName);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, sectionWriter.LastFilter.Profile);
        CollectionAssert.AreEqual(KeepFileNamesGit, sectionWriter.LastFilter.KeepFileNames!.ToArray());
    }
}
