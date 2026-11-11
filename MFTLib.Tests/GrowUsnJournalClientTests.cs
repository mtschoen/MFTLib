using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class GrowUsnJournalClientTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task GrowUsnJournalAsync_Success_SendsRequestAndReturnsSettings()
    {
        var received = new List<(VolumeKey Volume, long MaximumSize, long AllocationDelta, bool AllowAboveMaximumRecommendedSize)>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (volume, maximumSize, allocationDelta, allowAboveMaximumRecommendedSize) =>
            {
                received.Add((volume, maximumSize, allocationDelta, allowAboveMaximumRecommendedSize));
                return new UsnJournalGrowthResult
                {
                    Outcome = UsnJournalGrowthOutcome.GrewAsRequested,
                    RequestedMaximumSize = maximumSize,
                    MaximumRecommendedSize = 0x10000000L,
                    JournalSettings = new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta }
                };
            }
        }));

        var growth = await broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAsRequested, growth.Outcome);
        Assert.AreEqual(0x08000000L, growth.RequestedMaximumSize);
        Assert.AreEqual(0x10000000L, growth.MaximumRecommendedSize);
        Assert.AreEqual(0x08000000L, growth.JournalSettings.MaximumSize);
        Assert.AreEqual(0x01000000L, growth.JournalSettings.AllocationDelta);
        Assert.HasCount(1, received);
        Assert.AreEqual(new VolumeKey('C'), received[0].Volume);
        Assert.AreEqual(0x08000000L, received[0].MaximumSize);
        Assert.AreEqual(0x01000000L, received[0].AllocationDelta);
        Assert.IsFalse(received[0].AllowAboveMaximumRecommendedSize);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_DefaultRoot_ThrowsArgumentException()
    {
        await using var broker = new ScriptedBroker();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            broker.Process.GrowUsnJournalAsync(default, new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None));
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_ErrorFrame_ThrowsWithTheRefusalMessage()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (_, _, _, _) =>
                throw new InvalidOperationException("Unusable volume geometry.")
        }));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 32, AllocationDelta = 16 }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("Unusable volume geometry", exception.Message);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_BrokerDisconnects_Throws()
    {
        await using var broker = new ScriptedBroker();
        var request = broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None);
        var sent = await broker.ReadRequestAsync();
        await broker.CloseControlAsync();

        var exception = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => request.WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, sent.Kind);
        Assert.AreEqual(new VolumeKey('C'), sent.RequireVolume());
        Assert.AreEqual(0x08000000L, sent.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, sent.JournalAllocationDelta);
        Assert.IsFalse(sent.JournalAllowAboveMaximumRecommendedSize);
        Assert.IsNull(exception.Volume);
        Assert.IsFalse(string.IsNullOrEmpty(exception.Message));
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_NonPositiveSizes_AreForwardedAndTheHostRefusalIsThrown()
    {
        // The client does not validate sizes (BrokerProcess.Control.cs, GrowUsnJournalAsync): the
        // volume behind the host does, and its refusal returns as an Error frame.
        var received = new List<(long MaximumSize, long AllocationDelta)>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (_, maximumSize, allocationDelta, _) =>
            {
                received.Add((maximumSize, allocationDelta));
                throw new ArgumentException("positive sizes are required");
            }
        }));

        var zero = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0, AllocationDelta = 0x01000000 }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
        var negative = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = -1 }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("positive sizes are required", zero.Message);
        Assert.Contains("positive sizes are required", negative.Message);
        Assert.AreSequenceEqual([(0L, 0x01000000L), (0x08000000L, -1L)], received);
    }

    [TestMethod]
    [DataRow(false, UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended, 8L * 1024 * 1024 * 1024)]
    [DataRow(true, UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride, (8L * 1024 * 1024 * 1024) + 1)]
    [DataRow(false, UsnJournalGrowthOutcome.Unchanged, 0x200000L)]
    public async Task GrowUsnJournalAsync_OverCap_ForwardsOverrideAndSurfacesOutcomeThroughBrokerSession(
        bool allowAboveMaximumRecommendedSize,
        UsnJournalGrowthOutcome expectedOutcome,
        long expectedAppliedMaximum)
    {
        const long requested = (8L * 1024 * 1024 * 1024) + 1;
        const long recommended = 8L * 1024 * 1024 * 1024;
        var capturedOverrides = new List<bool>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (_, maximumSize, allocationDelta, overrideCap) =>
            {
                capturedOverrides.Add(overrideCap);
                return new UsnJournalGrowthResult
                {
                    Outcome = expectedOutcome,
                    RequestedMaximumSize = maximumSize,
                    MaximumRecommendedSize = recommended,
                    JournalSettings = new UsnJournalSettings
                    {
                        MaximumSize = expectedAppliedMaximum,
                        AllocationDelta = allocationDelta
                    }
                };
            }
        }));
        var handle = broker.Handle;
        await using var session = BrokerTestHarness.CreateSession(_ => Task.FromResult(handle));

        var result = await session.GrowUsnJournalAsync(
                'C',
                new UsnJournalSettings { MaximumSize = requested, AllocationDelta = 0x100000 },
                CancellationToken.None,
                allowAboveMaximumRecommendedSize)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSequenceEqual([allowAboveMaximumRecommendedSize], capturedOverrides);
        Assert.AreEqual(expectedOutcome, result.Outcome);
        Assert.AreEqual(requested, result.RequestedMaximumSize);
        Assert.AreEqual(recommended, result.MaximumRecommendedSize);
        Assert.AreEqual(expectedAppliedMaximum, result.JournalSettings.MaximumSize);
        Assert.AreEqual(0x100000L, result.JournalSettings.AllocationDelta);
    }

    public TestContext TestContext { get; set; } = null!;
}
