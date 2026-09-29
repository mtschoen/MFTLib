using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostSourcesTests
{
    [TestMethod]
    public void HostScanChunk_OneKilobyteRecords_Is65536()
    {
        Assert.AreEqual(65536u, JournalBrokerHost.HostScanChunkRecords(1024));
    }

    [TestMethod]
    public void HostScanChunk_FourKilobyteRecords_Is16384()
    {
        Assert.AreEqual(16384u, JournalBrokerHost.HostScanChunkRecords(4096));
    }

    [TestMethod]
    public void HostScanChunk_RecordLargerThanChunk_IsOneRecord()
    {
        Assert.AreEqual(1u, JournalBrokerHost.HostScanChunkRecords(JournalBrokerHost.HostScanChunkBytes * 2));
    }

    [TestMethod]
    public void HostScanChunk_NoRecordSize_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => JournalBrokerHost.HostScanChunkRecords(0));
    }

    [TestMethod]
    public void OperationState_RecordsEachPublicationAndWhenItHappened()
    {
        var clock = new FakeTimeProvider();
        var state = new BrokerOperationState(clock);
        Assert.AreEqual(BrokerOperationPhase.Idle, state.Current.Phase);

        clock.Advance(TimeSpan.FromSeconds(3));
        state.Queued();
        Assert.AreEqual(BrokerOperationPhase.Queued, state.Current.Phase);
        Assert.AreEqual(clock.GetTimestamp(), state.Current.Since);

        clock.Advance(TimeSpan.FromSeconds(2));
        state.Processing("MFT parse");
        Assert.AreEqual(BrokerOperationPhase.Processing, state.Current.Phase);
        Assert.AreEqual("MFT parse", state.Current.Step);
        Assert.AreEqual(clock.GetTimestamp(), state.Current.Since);

        state.WaitingOnVolume();
        Assert.AreEqual(BrokerOperationPhase.WaitingOnVolume, state.Current.Phase);
        Assert.IsNull(state.Current.Step);
    }
}
