using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Host-side handling of <see cref="BrokerFrameKind.GrowUsnJournal" />:
///     one <see cref="BrokerFrameKind.UsnJournalGrowthResult" /> or <see cref="BrokerFrameKind.Error" />
///     reply per request, carrying the request's id.
/// </summary>
[TestClass]
public class GrowUsnJournalHostTests
{
    [TestMethod]
    public async Task GrowUsnJournal_SeamReturnsGrowthResult_EmitsUsnJournalGrowthResult()
    {
        await using var harness = new HostChannelHarness(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => default,
            ScanVolume = (_, _, _, _, _, _) => [],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null,
            GrowUsnJournal = (_, maximumSize, allocationDelta, allowAboveMaximumRecommendedSize) =>
                new UsnJournalGrowthResult
                {
                    Outcome = allowAboveMaximumRecommendedSize
                        ? UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride
                        : UsnJournalGrowthOutcome.GrewAsRequested,
                    RequestedMaximumSize = maximumSize,
                    MaximumRecommendedSize = 0x10000000L,
                    JournalSettings = new UsnJournalSettings
                    {
                        MaximumSize = maximumSize,
                        AllocationDelta = allocationDelta
                    }
                }
        }));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(31, 'C', 0x08000000, 0x01000000, allowAboveMaximumRecommendedSize: true)));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalGrowthResult, frame.Kind);
        Assert.AreEqual(31u, frame.RequestId);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride, frame.JournalGrowthOutcome);
        Assert.AreEqual(0x08000000L, frame.JournalRequestedMaximumSize);
        Assert.AreEqual(0x10000000L, frame.JournalMaximumRecommendedSize);
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
    }

    [TestMethod]
    public async Task GrowUsnJournal_SeamThrows_EmitsErrorWithMessage()
    {
        await using var harness = new HostChannelHarness(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => default,
            ScanVolume = (_, _, _, _, _, _) => [],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null,
            GrowUsnJournal = (_, _, _, _) =>
                throw new InvalidOperationException("Refusing to resize: invalid geometry.")
        }));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(32, 'C', 0x08000000, 0x01000000)));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(32u, frame.RequestId);
        Assert.AreEqual("Refusing to resize: invalid geometry.", frame.Message);
    }

    [TestMethod]
    public async Task GrowUsnJournal_UnsupportedOperation_EmitsError()
    {
        await using var harness = new HostChannelHarness(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => default,
            ScanVolume = (_, _, _, _, _, _) => [],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        }));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(33, 'C', 0x08000000, 0x01000000)));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(33u, frame.RequestId);
        Assert.AreEqual("Broker has no journal grow source", frame.Message);
    }
}
