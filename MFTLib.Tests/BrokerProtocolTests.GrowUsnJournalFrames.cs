using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Round-trip and golden wire bytes for the <see cref="BrokerFrameKind.GrowUsnJournal" />
///     request (request id, volume, sizes) and the <see cref="BrokerFrameKind.UsnJournalGrowthResult" />
///     reply (request id, outcome, sizes), both on the control pipe.
/// </summary>
public partial class BrokerProtocolTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GrowUsnJournalFrame_RoundTrips_RequestIdVolumeSizesAndOverride(bool allowAboveMaximumRecommendedSize)
    {
        var frame = RoundTrip(
            BrokerFrame.GrowUsnJournal(12, 'c', 0x08000000, 0x01000000, allowAboveMaximumRecommendedSize),
            BrokerProtocol.WriteGrowUsnJournal);

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, frame.Kind);
        Assert.AreEqual(12u, frame.RequestId);
        Assert.AreEqual(new VolumeKey('C'), frame.RequireVolume());
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
        Assert.AreEqual(allowAboveMaximumRecommendedSize, frame.JournalAllowAboveMaximumRecommendedSize);
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    [DataRow(UsnJournalGrowthOutcome.GrewAsRequested)]
    [DataRow(UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended)]
    [DataRow(UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride)]
    [DataRow(UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget)]
    public void UsnJournalGrowthResultFrame_RoundTrips_RequestIdOutcomeAndSizes(UsnJournalGrowthOutcome outcome)
    {
        var expected = new UsnJournalGrowthResult
        {
            Outcome = outcome,
            RequestedMaximumSize = 0x20000000L,
            MaximumRecommendedSize = 0x10000000L,
            JournalSettings = new UsnJournalSettings
            {
                MaximumSize = 0x10000000L,
                AllocationDelta = 0x02000000L
            }
        };
        var frame = RoundTrip(BrokerFrame.UsnJournalGrowthResult(12, expected), BrokerProtocol.WriteUsnJournalGrowthResult);

        Assert.AreEqual(BrokerFrameKind.UsnJournalGrowthResult, frame.Kind);
        Assert.AreEqual(12u, frame.RequestId);
        Assert.AreEqual(expected, frame.ToJournalGrowthResult());
        Assert.IsNull(frame.Volume, "The reply is matched by its request id, not by a volume.");
        Assert.IsEmpty(frame.Entries);
    }

    [TestMethod]
    public void WireBytes_Golden_GrowUsnJournalFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteGrowUsnJournal(w, BrokerFrame.GrowUsnJournal(5, 'c', 0x08000000, 0x01000000, allowAboveMaximumRecommendedSize: true)),
        [
            0x18, 0x00, 0x00, 0x00, // totalLength = 24
            0x05, // kind = GrowUsnJournal
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x43, 0x00, // volume 'C'
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, // allocationDelta = 0x01000000
            0x01 // allowAboveMaximumRecommendedSize = true
        ]);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.ThrowsExactly<ArgumentException>(() => BrokerProtocol.WriteGrowUsnJournal(buffer, BrokerFrame.GrowUsnJournal(5, default, 0x08000000, 0x01000000)));
        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void WireBytes_Golden_UsnJournalGrowthResultFrame()
    {
        var growth = new UsnJournalGrowthResult
        {
            Outcome = UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended,
            RequestedMaximumSize = 0x10000000,
            MaximumRecommendedSize = 0x08000000,
            JournalSettings = new UsnJournalSettings
            {
                MaximumSize = 0x08000000,
                AllocationDelta = 0x01000000
            }
        };
        AssertWireBytes(w => BrokerProtocol.WriteUsnJournalGrowthResult(w, BrokerFrame.UsnJournalGrowthResult(5, growth)),
        [
            0x26, 0x00, 0x00, 0x00, // totalLength = 38
            0x06, // kind = UsnJournalGrowthResult
            0x05, 0x00, 0x00, 0x00, // requestId = 5
            0x01, // outcome = GrewClampedToMaximumRecommended
            0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, // requestedMaximumSize = 0x10000000
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumRecommendedSize = 0x08000000
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, // maximumSize = 0x08000000
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 // allocationDelta = 0x01000000
        ]);

        var invalidBuffer = new System.Buffers.ArrayBufferWriter<byte>();
        var invalidGrowth = growth with { Outcome = (UsnJournalGrowthOutcome)99 };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            BrokerProtocol.WriteUsnJournalGrowthResult(invalidBuffer, BrokerFrame.UsnJournalGrowthResult(5, invalidGrowth)));
        Assert.AreEqual(0, invalidBuffer.WrittenCount);
    }
}
