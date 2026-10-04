using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Round-trip and golden wire bytes for the <see cref="BrokerFrameKind.GrowUsnJournal" />
///     request (request id, drive, sizes) and the <see cref="BrokerFrameKind.UsnJournalSettings" />
///     reply (request id, sizes), both on the control pipe.
/// </summary>
public partial class BrokerProtocolTests
{
    [TestMethod]
    public void GrowUsnJournalFrame_RoundTrips_RequestIdDriveAndSizes()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 12, "C", 0x08000000, 0x01000000));

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, frame.Kind);
        Assert.AreEqual(12u, frame.RequestId);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
    }

    [TestMethod]
    public void UsnJournalSettingsFrame_RoundTrips_RequestIdAndSizes()
    {
        var frame = RoundTrip(writer => BrokerProtocol.WriteUsnJournalSettings(writer, 12, 0x10000000, 0x02000000));

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual(12u, frame.RequestId);
        Assert.AreEqual(0x10000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x02000000L, frame.JournalAllocationDelta);
        Assert.IsNull(frame.Drive, "The reply is matched by its request id, not by a drive.");
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
    }

    [TestMethod]
    public void WireBytes_Golden_GrowUsnJournalFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteGrowUsnJournal(w, 5, "C", 0x08000000, 0x01000000),
        [
            0x1B, 0x00, 0x00, 0x00, // totalLength = 27
            0x05, // kind = GrowUsnJournal
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_UsnJournalSettingsFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteUsnJournalSettings(w, 5, 0x08000000, 0x01000000),
        [
            0x15, 0x00, 0x00, 0x00, // totalLength = 21
            0x06, // kind = UsnJournalSettings
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);
    }
}
