using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Round-trip and golden wire bytes for the <see cref="BrokerFrameKind.GrowUsnJournal" />
///     request and <see cref="BrokerFrameKind.UsnJournalSettings" /> reply frames
///     (identical payload shape: drive, maximumSize i64, allocationDelta i64).
/// </summary>
public partial class BrokerProtocolTests
{
    [TestMethod]
    public void GrowUsnJournalFrame_RoundTrips_DriveAndSizes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteGrowUsnJournal(buffer, "C", 0x08000000, 0x01000000);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void UsnJournalSettingsFrame_RoundTrips_DriveAndSizes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteUsnJournalSettings(buffer, "D:\\", 0x10000000, 0x02000000);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual("D:\\", frame.Drive);
        Assert.AreEqual(0x10000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x02000000L, frame.JournalAllocationDelta);
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void WireBytes_Golden_GrowUsnJournalFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteGrowUsnJournal(w, "C", 0x08000000, 0x01000000),
        [
            0x17, 0x00, 0x00, 0x00, // totalLength = 23
            0x10, // kind = GrowUsnJournal (16)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);
    }

    [TestMethod]
    public void WireBytes_Golden_UsnJournalSettingsFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteUsnJournalSettings(w, "C", 0x08000000, 0x01000000),
        [
            0x17, 0x00, 0x00, 0x00, // totalLength = 23
            0x11, // kind = UsnJournalSettings (17)
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);
    }

    [TestMethod]
    public void Factory_GrowUsnJournal_PopulatesKindDriveAndSizes()
    {
        var frame = BrokerFrame.GrowUsnJournal("C", 1, 2);
        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(1L, frame.JournalMaximumSize);
        Assert.AreEqual(2L, frame.JournalAllocationDelta);
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
    }

    [TestMethod]
    public void Factory_UsnJournalSettings_PopulatesKindDriveAndSizes()
    {
        var frame = BrokerFrame.UsnJournalSettings("C", 1, 2);
        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(1L, frame.JournalMaximumSize);
        Assert.AreEqual(2L, frame.JournalAllocationDelta);
        Assert.AreEqual(0, frame.Entries.Length);
        Assert.AreEqual(0, frame.KeepFileNames.Count);
    }
}
