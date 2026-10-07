using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostSourcesTests
{
    [TestMethod]
    public void HostScanChunk_OneKilobyteRecords_Is65536()
    {
        Assert.AreEqual(65536u, LiveVolumeSources.HostScanChunkRecords(1024));
    }

    [TestMethod]
    public void HostScanChunk_FourKilobyteRecords_Is16384()
    {
        Assert.AreEqual(16384u, LiveVolumeSources.HostScanChunkRecords(4096));
    }

    [TestMethod]
    public void HostScanChunk_RecordLargerThanChunk_IsOneRecord()
    {
        Assert.AreEqual(1u, LiveVolumeSources.HostScanChunkRecords(LiveVolumeSources.HostScanChunkBytes * 2));
    }

    [TestMethod]
    public void HostScanChunk_NoRecordSize_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => LiveVolumeSources.HostScanChunkRecords(0));
    }

    [TestMethod]
    public void OperationState_RecordsEachPublicationAndWhenItHappened()
    {
        var clock = new FakeTimeProvider();
        var published = new List<(string Tag, ChannelOperationState State)>();
        using var owner = new CancellationTokenSource();
        var pipe = new HostPipeWriter(Stream.Null, "pipe", clock, heartbeatsWhenIdle: false, owner,
            (tag, state) => published.Add((tag, state)));

        clock.Advance(TimeSpan.FromSeconds(3));
        pipe.Queued();
        Assert.AreEqual(new ChannelOperationState(ChannelOperationKind.Queued, string.Empty, clock.GetUtcNow()),
            published[^1].State);

        clock.Advance(TimeSpan.FromSeconds(2));
        pipe.Processing("MFT parse");
        Assert.AreEqual(new ChannelOperationState(ChannelOperationKind.Processing, "MFT parse", clock.GetUtcNow()),
            published[^1].State);

        pipe.WaitingOnVolume();
        Assert.AreEqual(new ChannelOperationState(ChannelOperationKind.WaitingOnVolume, string.Empty, clock.GetUtcNow()),
            published[^1].State);
        Assert.AreEqual(3, published.Count);
        Assert.IsTrue(published.All(entry => entry.Tag == "pipe"));
    }
}
