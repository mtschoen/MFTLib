using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Round-trip and golden wire bytes for the <see cref="BrokerFrameKind.GrowUsnJournal" />
///     request (request id, volume, sizes) and the <see cref="BrokerFrameKind.UsnJournalSettings" />
///     reply (request id, sizes), both on the control pipe.
/// </summary>
public partial class BrokerProtocolTests
{
    [TestMethod]
    public void GrowUsnJournalFrame_RoundTrips_RequestIdVolumeAndSizes()
    {
        var frame = RoundTrip(BrokerFrame.GrowUsnJournal(12, 'c', 0x08000000, 0x01000000), BrokerProtocol.WriteGrowUsnJournal);

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, frame.Kind);
        Assert.AreEqual(12u, frame.RequestId);
        Assert.AreEqual(new VolumeKey('C'), frame.RequireVolume());
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void UsnJournalSettingsFrame_RoundTrips_RequestIdAndSizes()
    {
        var frame = RoundTrip(BrokerFrame.UsnJournalSettings(12, 0x10000000, 0x02000000), BrokerProtocol.WriteUsnJournalSettings);

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual(12u, frame.RequestId);
        Assert.AreEqual(0x10000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x02000000L, frame.JournalAllocationDelta);
        Assert.IsNull(frame.Volume, "The reply is matched by its request id, not by a volume.");
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void WireBytes_Golden_GrowUsnJournalFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteGrowUsnJournal(w, BrokerFrame.GrowUsnJournal(5, 'c', 0x08000000, 0x01000000)),
        [
            0x17, 0x00, 0x00, 0x00, // totalLength = 23
            0x05, // kind = GrowUsnJournal
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x43, 0x00, // volume 'C'
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.ThrowsExactly<ArgumentException>(() => BrokerProtocol.WriteGrowUsnJournal(buffer, BrokerFrame.GrowUsnJournal(5, default, 0x08000000, 0x01000000)));
        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void WireBytes_Golden_UsnJournalSettingsFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteUsnJournalSettings(w, BrokerFrame.UsnJournalSettings(5, 0x08000000, 0x01000000)),
        [
            0x15, 0x00, 0x00, 0x00, // totalLength = 21
            0x06, // kind = UsnJournalSettings
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);
    }
}
