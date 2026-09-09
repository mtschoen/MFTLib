using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class BrokerProtocolTests
{
    static readonly string[] KeepFileNamesGitAndNonAscii = [".git", "repört"];
    static readonly string[] KeepFileNamesGit = [".git"];
    static readonly string[] KeepFileNamesSingleLetter = ["a"];

    static void AssertWireBytes(Action<ArrayBufferWriter<byte>> write, byte[] expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        CollectionAssert.AreEqual(expected, buffer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void Factory_JournalBatch_PopulatesDriveCursorEntries()
    {
        var cursor = new UsnJournalCursor(7UL, 110L);
        var entries = new[]
        {
            JournalEntryFactory.Create(1, 10, "a")
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

    [TestMethod]
    public void WarningFrame_RoundTrips_PerDriveMessage()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteWarning(buffer, "D:\\", "catch-up failed");
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.Warning, frame.Kind);
        Assert.AreEqual("D:\\", frame.Drive);
        Assert.AreEqual("catch-up failed", frame.Message);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void WireBytes_Golden_WarningFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteWarning(w, "C", "D"),
        [
            0x0D, 0x00, 0x00, 0x00, // totalLength = 13
            0x0C, // kind = Warning (12)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x02, 0x00, 0x00, 0x00, 0x44, 0x00 // message "D"
        ]);
    }

    [TestMethod]
    public void Factory_Warning_PopulatesDriveAndMessage()
    {
        var frame = BrokerFrame.Warning("D", "catch-up failed");
        Assert.AreEqual(BrokerFrameKind.Warning, frame.Kind);
        Assert.AreEqual("D", frame.Drive);
        Assert.AreEqual("catch-up failed", frame.Message);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void ScanProgressFrame_RoundTrips_AllFields()
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
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteScanProgress(buffer, progress);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.IsNotNull(frame.Progress);
        Assert.AreEqual(progress, frame.Progress.Value);
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress.Value.Phase);
        Assert.AreEqual(buffer.WrittenCount, consumed);
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
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteScanProgress(buffer, progress);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual("D", frame.Drive);
        Assert.IsNotNull(frame.Progress);
        Assert.AreEqual(progress, frame.Progress.Value);
        Assert.AreEqual(BrokerScanPhase.Transferring, frame.Progress.Value.Phase);
        Assert.IsNull(frame.Progress.Value.TotalRecords);
        Assert.IsNull(frame.Progress.Value.TotalBytes);
        Assert.AreEqual(buffer.WrittenCount, consumed);
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
            0x33, 0x00, 0x00, 0x00, // totalLength = 51 (1 + 4 + 2 + 4 + 8*5 = 51)
            0x0B, // kind = ScanProgress (11)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C" (UTF-16)
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
            0x33, 0x00, 0x00, 0x00, // totalLength = 51
            0x0B, // kind = ScanProgress (11)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x00, 0x00, 0x00, 0x00, // phase = Parsing (0)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 2
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalRecords = -1
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalBytes = -1
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // elapsedTicks = 3
        ]);
    }

    [TestMethod]
    public void Factory_ScanProgress_PopulatesDriveAndProgress()
    {
        var progress = new BrokerScanProgress("E", 10, 20, 30, 40, TimeSpan.FromSeconds(1));
        var frame = BrokerFrame.ScanProgress(progress);
        Assert.AreEqual(BrokerFrameKind.ScanProgress, frame.Kind);
        Assert.AreEqual("E", frame.Drive);
        Assert.AreEqual(progress, frame.Progress);
        Assert.AreEqual(BrokerScanPhase.Parsing, frame.Progress!.Value.Phase);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void ReadFrame_ScanProgress_InvalidPhase_ThrowsInvalidDataException()
    {
        // Wire bytes with invalid phase = 99
        byte[] payload =
        [
            0x33, 0x00, 0x00, 0x00, // totalLength = 51
            0x0B, // kind = ScanProgress (11)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x63, 0x00, 0x00, 0x00, // phase = 99 (invalid)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // recordsProcessed = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // bytesProcessed = 2
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalRecords = -1
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // totalBytes = -1
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // elapsedTicks = 3
        ];

        Assert.ThrowsException<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(payload, out _));
    }

    [TestMethod]
    public void ReadFrame_ScanProgress_PhaseOutsideByteRange_ThrowsInvalidDataException()
    {
        byte[] payload =
        [
            0x33, 0x00, 0x00, 0x00,
            0x0B,
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00,
            0x00, 0x01, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(payload, out _));
    }

    [TestMethod]
    public void WriteScanProgress_DefaultValue_ThrowsArgumentNullException()
    {
        var buffer = new ArrayBufferWriter<byte>();

        var exception = Assert.ThrowsException<ArgumentNullException>(() =>
            BrokerProtocol.WriteScanProgress(buffer, default));

        StringAssert.Contains(exception.Message, "DriveLetter");
    }

    [TestMethod]
    public void WriteScanProgress_EmptyDriveLetter_ThrowsArgumentException()
    {
        var buffer = new ArrayBufferWriter<byte>();

        var exception = Assert.ThrowsException<ArgumentException>(() =>
            BrokerProtocol.WriteScanProgress(buffer, new BrokerScanProgress { DriveLetter = string.Empty }));

        StringAssert.Contains(exception.Message, "DriveLetter");
    }

    // ---------------------------------------------------------------------------
    // QueryVolumes / VolumeInfo (MFTLib#97)
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void QueryVolumesFrame_RoundTrips_DrivesSpec()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteQueryVolumes(buffer, "C:0:0,D:0:0");
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.QueryVolumes, frame.Kind);
        Assert.AreEqual("C:0:0,D:0:0", frame.DrivesSpec);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void VolumeInfoFrame_RoundTrips_AllFields()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteVolumeInfo(buffer, "C", 8_000_000, 1024, 8_192_000_000);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(8_000_000L, frame.RecordCount);
        Assert.AreEqual(1024U, frame.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, frame.MftValidDataLength);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void WireBytes_Golden_QueryVolumesFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteQueryVolumes(w, "C"),
            [0x07, 0x00, 0x00, 0x00, 0x0D, 0x02, 0x00, 0x00, 0x00, 0x43, 0x00]);
    }

    [TestMethod]
    public void WireBytes_Golden_VolumeInfoFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteVolumeInfo(w, "C", 1, 2, 3),
        [
            0x1B, 0x00, 0x00, 0x00, // totalLength = 27 (1 + 6 + 8 + 4 + 8)
            0x0E, // kind = VolumeInfo (14)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // mftRecordCount = 1
            0x02, 0x00, 0x00, 0x00, // bytesPerFileRecordSegment = 2
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // mftValidDataLength = 3
        ]);
    }

    [TestMethod]
    public void Factory_QueryVolumes_PopulatesDrivesSpec()
    {
        var frame = BrokerFrame.QueryVolumes("C:0:0,D:0:0");
        Assert.AreEqual(BrokerFrameKind.QueryVolumes, frame.Kind);
        Assert.AreEqual("C:0:0,D:0:0", frame.DrivesSpec);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void Factory_VolumeInfo_PopulatesAllFields()
    {
        var frame = BrokerFrame.VolumeInfo("C", 8_000_000, 1024, 8_192_000_000);
        Assert.AreEqual(BrokerFrameKind.VolumeInfo, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(8_000_000L, frame.RecordCount);
        Assert.AreEqual(1024U, frame.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, frame.MftValidDataLength);
        Assert.AreEqual(0, frame.Entries.Length);
    }
}
