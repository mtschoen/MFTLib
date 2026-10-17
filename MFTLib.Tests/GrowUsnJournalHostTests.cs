using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Host-side handling of <see cref="BrokerFrameKind.GrowUsnJournal" />:
///     one <see cref="BrokerFrameKind.UsnJournalSettings" /> or <see cref="BrokerFrameKind.Error" />
///     reply per request, carrying the request's id.
/// </summary>
[TestClass]
public class GrowUsnJournalHostTests
{
    [TestMethod]
    public async Task GrowUsnJournal_SeamReturnsSettings_EmitsUsnJournalSettings()
    {
        await using var harness = new HostChannelHarness(CreateHost(growUsnJournal: (_, maximumSize, allocationDelta) =>
            new UsnJournalSettings
            {
                MaximumSize = maximumSize,
                AllocationDelta = allocationDelta
            }));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 31, 'C', 0x08000000, 0x01000000));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual(31u, frame.RequestId);
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
    }

    [TestMethod]
    public async Task GrowUsnJournal_SeamThrows_EmitsErrorWithMessage()
    {
        await using var harness = new HostChannelHarness(CreateHost(growUsnJournal: (_, _, _) =>
            throw new InvalidOperationException("Refusing to resize: only growth is permitted.")));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 32, 'C', 0x08000000, 0x01000000));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(32u, frame.RequestId);
        Assert.AreEqual("Refusing to resize: only growth is permitted.", frame.Message);
    }

    [TestMethod]
    public async Task GrowUsnJournal_UnsupportedOperation_EmitsError()
    {
        await using var harness = new HostChannelHarness(CreateHost()); // growUsnJournal omitted -> unsupported throws

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 33, 'C', 0x08000000, 0x01000000));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(33u, frame.RequestId);
        Assert.AreEqual("Broker has no journal grow source", frame.Message);
    }

    static JournalBrokerHost CreateHost(Func<DriveKey, long, long, UsnJournalSettings>? growUsnJournal = null)
    {
        return new JournalBrokerHost(new ScriptedVolumeAccess
        {
            CursorQuery = _ => default,
            RecordBatches = (_, _, _, _, _, _) => [],
            JournalRead = (_, cursor, _) => (Array.Empty<UsnJournalEntry>(), cursor),
            JournalGrow = growUsnJournal ?? ScriptedVolumeAccess.UnsupportedGrow
        });
    }
}
