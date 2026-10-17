using MFTLib.Tests.TestSupport;
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

    [DataTestMethod]
    [DataRow("queued", false)]
    [DataRow("waiting", false)]
    [DataRow("processing", true)]
    public async Task OperationState_DeterminesHeartbeatAndStall(string state, bool stalls)
    {
        var clock = new FakeTimeProvider();
        using var owner = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var pipe = new HostPipeWriter(stream, "pipe", clock, heartbeatsWhenIdle: false, owner);
        try
        {
            switch (state)
            {
                case "queued": pipe.Queued(); break;
                case "waiting": pipe.WaitingOnVolume(); break;
                case "processing": pipe.Processing("MFT parse"); break;
                default: Assert.Fail("Unknown test state."); break;
            }
            clock.Advance(BrokerLiveness.ProcessingLimit);
            pipe.Visit(clock.GetUtcNow());
            stream.Position = 0;
            var frame = await HostChannelHarness.ReadFrameAsync(stream, includeHeartbeats: true)
                ?? throw new AssertFailedException("The writer emitted no frame.");
            Assert.AreEqual(stalls ? BrokerFrameKind.Stalled : BrokerFrameKind.Heartbeat, frame.Kind);
            Assert.AreEqual(stalls, owner.IsCancellationRequested);
            if (stalls)
            {
                StringAssert.Contains(frame.Message, "MFT parse");
                StringAssert.Contains(frame.Message, "30 seconds");
            }
        }
        finally
        {
            pipe.Close();
        }
    }
}
