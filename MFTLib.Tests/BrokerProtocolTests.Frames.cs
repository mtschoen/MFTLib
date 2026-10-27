using System.Buffers;
using MFTLib.Index;
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
            TimestampUtc = new DateTime(2026, 6, 20, 1, 2, 3, DateTimeKind.Utc),
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
        Assert.AreEqual(entry.TimestampUtc, read.TimestampUtc);
        Assert.AreEqual(entry.Reason, read.Reason);
        Assert.AreEqual(entry.FileAttributes, read.FileAttributes);
        Assert.AreEqual(entry.FileName, read.FileName);
    }

    [TestMethod]
    public void OpenChannelFrame_RoundTrips_RequestIdDriveAndPipeName()
    {
        var frame = RoundTrip(BrokerFrame.OpenChannel(0xDEADBEEF, 'C', "mftlib-drive-C-1"), BrokerProtocol.WriteOpenChannel);

        Assert.AreEqual(BrokerFrameKind.OpenChannel, frame.Kind);
        Assert.AreEqual(0xDEADBEEF, frame.RequestId);
        Assert.AreEqual(new VolumeKey('C'), frame.RequireDrive());
        Assert.AreEqual("mftlib-drive-C-1", frame.PipeName);
        Assert.IsEmpty(frame.Entries);
        Assert.IsNull(frame.RetainedFileNames);
    }

    [TestMethod]
    public void ChannelOpenedFrame_RoundTrips_RequestId()
    {
        var frame = RoundTrip(BrokerFrame.ChannelOpened(17), BrokerProtocol.WriteChannelOpened);

        Assert.AreEqual(BrokerFrameKind.ChannelOpened, frame.Kind);
        Assert.AreEqual(17u, frame.RequestId);
    }

    [TestMethod]
    public void QueryVolumeFrame_RoundTrips_RequestIdAndDrive()
    {
        var frame = RoundTrip(BrokerFrame.QueryVolume(4, 'D'), BrokerProtocol.WriteQueryVolume);

        Assert.AreEqual(BrokerFrameKind.QueryVolume, frame.Kind);
        Assert.AreEqual(4u, frame.RequestId);
        Assert.AreEqual(new VolumeKey('D'), frame.RequireDrive());
    }

    [TestMethod]
    public void VolumeInfoFrame_RoundTrips_RequestIdAndAllFields()
    {
        var frame = RoundTrip(BrokerFrame.VolumeInfo(9, 1024, 8_192_000_000), BrokerProtocol.WriteVolumeInfo);

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, frame.Kind);
        Assert.AreEqual(9u, frame.RequestId);
        Assert.AreEqual(1024U, frame.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, frame.MftValidDataLength);
        Assert.IsNull(frame.Drive, "The reply is matched by its request id, not by a drive.");
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void ErrorFrame_RoundTrips_RequestIdAndMessage()
    {
        var frame = RoundTrip(BrokerFrame.Error(42, "journal wrapped"), BrokerProtocol.WriteError);

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(42u, frame.RequestId);
        Assert.AreEqual("journal wrapped", frame.Message);
        Assert.IsNull(frame.Drive);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void ErrorFrame_OnDrivePipe_RoundTripsRequestIdZero()
    {
        var frame = RoundTrip(BrokerFrame.Error(0, "no watch source"), BrokerProtocol.WriteError);

        Assert.AreEqual(0u, frame.RequestId);
        Assert.AreEqual("no watch source", frame.Message);
    }

    [TestMethod]
    public void HeartbeatFrame_RoundTrips_NoPayload()
    {
        var frame = RoundTrip(BrokerFrame.Heartbeat(), BrokerProtocol.WriteHeartbeat);

        Assert.AreEqual(BrokerFrameKind.Heartbeat, frame.Kind);
        Assert.AreEqual(0u, frame.RequestId);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void StalledFrame_RoundTrips_Message()
    {
        var frame = RoundTrip(BrokerFrame.Stalled("no progress for 90 seconds"), BrokerProtocol.WriteStalled);

        Assert.AreEqual(BrokerFrameKind.Stalled, frame.Kind);
        Assert.AreEqual("no progress for 90 seconds", frame.Message);
    }

    [TestMethod]
    public void WireBytes_Golden_NoPayloadFrames()
    {
        AssertWireBytes(w => BrokerProtocol.WriteHeartbeat(w, BrokerFrame.Heartbeat()), [0x01, 0x00, 0x00, 0x00, 0x08]);
        AssertWireBytes(w => BrokerProtocol.WriteCaughtUp(w, BrokerFrame.CaughtUp()), [0x01, 0x00, 0x00, 0x00, 0x11]);
    }

    [TestMethod]
    public void WireBytes_Golden_OpenChannelFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteOpenChannel(w, BrokerFrame.OpenChannel(1, 'c', "p")),
        [
            0x0D, 0x00, 0x00, 0x00, // totalLength = 13
            0x01, // kind = OpenChannel
            0x01, 0x00, 0x00, 0x00, // requestId = 1
            0x43, 0x00, // drive 'C'
            0x02, 0x00, 0x00, 0x00, 0x70, 0x00 // pipeName "p"
        ]);

        var buffer = new ArrayBufferWriter<byte>();
        Assert.ThrowsExactly<ArgumentException>(() => BrokerProtocol.WriteOpenChannel(buffer, BrokerFrame.OpenChannel(1, default, "p")));
        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void WireBytes_Golden_ChannelOpenedFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteChannelOpened(w, BrokerFrame.ChannelOpened(3)),
            [0x05, 0x00, 0x00, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00]);
    }

    [TestMethod]
    public void WireBytes_Golden_QueryVolumeFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteQueryVolume(w, BrokerFrame.QueryVolume(2, 'c')),
        [
            0x07, 0x00, 0x00, 0x00, // totalLength = 7
            0x03, // kind = QueryVolume
            0x02, 0x00, 0x00, 0x00, // requestId = 2
            0x43, 0x00 // drive 'C'
        ]);

        var buffer = new ArrayBufferWriter<byte>();
        Assert.ThrowsExactly<ArgumentException>(() => BrokerProtocol.WriteQueryVolume(buffer, BrokerFrame.QueryVolume(2, default)));
        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void WireBytes_Golden_VolumeInfoFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteVolumeInfo(w, BrokerFrame.VolumeInfo(5, 2, 3)),
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
        AssertWireBytes(w => BrokerProtocol.WriteError(w, BrokerFrame.Error(42, "D")),
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
        AssertWireBytes(w => BrokerProtocol.WriteStalled(w, BrokerFrame.Stalled("D")),
        [
            0x07, 0x00, 0x00, 0x00, // totalLength = 7
            0x09, // kind = Stalled
            0x02, 0x00, 0x00, 0x00, 0x44, 0x00 // message "D"
        ]);
    }
}
