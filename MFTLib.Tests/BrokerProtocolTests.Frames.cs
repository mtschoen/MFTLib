using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The frames of the control pipe (OpenChannel, ChannelOpened, QueryVolume, VolumeInfo) and the
// kinds that travel on any pipe (Error, Heartbeat, Stalled), plus the journal entry they share.
public partial class BrokerProtocolTests
{
    [TestMethod]
    public void JournalEntry_RoundTrips_AllFields()
    {
        var entry = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 42,
            ParentRecordNumber = 7,
            SequenceNumber = 169,
            Usn = 123456,
            Timestamp = new DateTime(2026, 6, 20, 1, 2, 3, DateTimeKind.Utc),
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = "repört.txt"
        }); // non-ASCII to prove UTF-16

        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEntry(buffer, entry);
        var read = BrokerProtocol.ReadEntry(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(buffer.WrittenCount, consumed);
        Assert.AreEqual(entry.RecordNumber, read.RecordNumber);
        Assert.AreEqual(entry.ParentRecordNumber, read.ParentRecordNumber);
        Assert.AreEqual(entry.SequenceNumber, read.SequenceNumber);
        Assert.AreEqual(entry.Usn, read.Usn);
        Assert.AreEqual(entry.Timestamp, read.Timestamp);
        Assert.AreEqual(entry.Reason, read.Reason);
        Assert.AreEqual(entry.FileAttributes, read.FileAttributes);
        Assert.AreEqual(entry.FileName, read.FileName);
    }

    [TestMethod]
    public void OpenChannelFrame_RoundTrips_RequestIdDriveAndPipeName()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteOpenChannel(writer, 0xDEADBEEF, "C", @"mftlib-drive-C-1"));

        Assert.AreEqual(BrokerFrameKind.OpenChannel, frame.Kind);
        Assert.AreEqual(0xDEADBEEF, frame.RequestId);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("mftlib-drive-C-1", frame.PipeName);
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
    }

    [TestMethod]
    public void ChannelOpenedFrame_RoundTrips_RequestId()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteChannelOpened(writer, 17));

        Assert.AreEqual(BrokerFrameKind.ChannelOpened, frame.Kind);
        Assert.AreEqual(17u, frame.RequestId);
    }

    [TestMethod]
    public void QueryVolumeFrame_RoundTrips_RequestIdAndDrive()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteQueryVolume(writer, 4, "D"));

        Assert.AreEqual(BrokerFrameKind.QueryVolume, frame.Kind);
        Assert.AreEqual(4u, frame.RequestId);
        Assert.AreEqual("D", frame.Drive);
    }

    [TestMethod]
    public void VolumeInfoFrame_RoundTrips_RequestIdAndAllFields()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteVolumeInfo(writer, 9, 1024, 8_192_000_000));

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, frame.Kind);
        Assert.AreEqual(9u, frame.RequestId);
        Assert.AreEqual(1024U, frame.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, frame.MftValidDataLength);
        Assert.IsNull(frame.Drive, "The reply is matched by its request id, not by a drive.");
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void ErrorFrame_RoundTrips_RequestIdAndMessage()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteError(writer, 42, "journal wrapped"));

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(42u, frame.RequestId);
        Assert.AreEqual("journal wrapped", frame.Message);
        Assert.IsNull(frame.Drive);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void ErrorFrame_OnDrivePipe_RoundTripsRequestIdZero()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteError(writer, 0, "no watch source"));

        Assert.AreEqual(0u, frame.RequestId);
        Assert.AreEqual("no watch source", frame.Message);
    }

    [TestMethod]
    public void HeartbeatFrame_RoundTrips_NoPayload()
    {
        var frame = RoundTrip(BrokerProtocol.WriteHeartbeat);

        Assert.AreEqual(BrokerFrameKind.Heartbeat, frame.Kind);
        Assert.AreEqual(0u, frame.RequestId);
        Assert.AreEqual(0, frame.Entries.Length);
    }

    [TestMethod]
    public void StalledFrame_RoundTrips_Message()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteStalled(writer, "no progress for 90 seconds"));

        Assert.AreEqual(BrokerFrameKind.Stalled, frame.Kind);
        Assert.AreEqual("no progress for 90 seconds", frame.Message);
    }

    [TestMethod]
    public void WireBytes_Golden_NoPayloadFrames()
    {
        AssertWireBytes(BrokerProtocol.WriteHeartbeat, [0x01, 0x00, 0x00, 0x00, 0x08]);
        AssertWireBytes(BrokerProtocol.WriteCaughtUp, [0x01, 0x00, 0x00, 0x00, 0x11]);
    }

    [TestMethod]
    public void WireBytes_Golden_OpenChannelFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteOpenChannel(w, 1, "C", "p"),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17
            0x01, // kind = OpenChannel
            0x01, 0x00, 0x00, 0x00, // requestId = 1
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x02, 0x00, 0x00, 0x00, 0x70, 0x00 // pipeName "p"
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ChannelOpenedFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteChannelOpened(w, 3),
            [0x05, 0x00, 0x00, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00]);
    }

    [TestMethod]
    public void WireBytes_Golden_QueryVolumeFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteQueryVolume(w, 2, "C"),
        [
            0x0B, 0x00, 0x00, 0x00, // totalLength = 11
            0x03, // kind = QueryVolume
            0x02, 0x00, 0x00, 0x00, // requestId = 2
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00 // drive "C"
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_VolumeInfoFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteVolumeInfo(w, 5, 2, 3),
        [
            0x11, 0x00, 0x00, 0x00, // totalLength = 17 (1 + 4 + 4 + 8)
            0x04, // kind = VolumeInfo
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x02, 0x00, 0x00, 0x00, // bytesPerFileRecordSegment = 2
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // mftValidDataLength = 3
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_ErrorFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteError(w, 42, "D"),
        [
            0x0B, 0x00, 0x00, 0x00, // totalLength = 11
            0x07, // kind = Error
            0x2A, 0x00, 0x00, 0x00, // requestId = 42
            0x02, 0x00, 0x00, 0x00, 0x44, 0x00 // message "D"
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_StalledFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteStalled(w, "D"),
        [
            0x07, 0x00, 0x00, 0x00, // totalLength = 7
            0x09, // kind = Stalled
            0x02, 0x00, 0x00, 0x00, 0x44, 0x00 // message "D"
        ]);
    }
}
