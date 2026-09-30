using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class VolumeQueryClientTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task QueryVolumeAsync_OneDriveSucceedsAndOneErrors_ReturnsTheVolumeAndThrowsTheHostMessage()
    {
        var reply = new NtfsVolumeInformation(8_192_000_000, 1024, 512, 4096, 100, 10);
        await using var broker = new InProcessBroker(CreateHost(queryVolumeInfo: drive => drive == "C"
            ? reply
            : throw new UnauthorizedAccessException("access denied")));

        var volume = await broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.QueryVolumeAsync('G', CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(8_000_000L, volume.MftRecordCount);
        Assert.AreEqual(1024U, volume.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, volume.MftValidDataLength);
        Assert.AreEqual(0U, volume.BytesPerSector);
        Assert.AreEqual(0U, volume.BytesPerCluster);
        Assert.AreEqual(0L, volume.TotalClusters);
        Assert.AreEqual(0L, volume.FreeClusters);
        Assert.AreEqual("access denied", exception.Message);
    }

    [TestMethod]
    public async Task QueryVolumeAsync_ControlPipeClosesAfterOneReply_AnsweredQuerySucceedsAndPendingQueryLosesTheProcess()
    {
        await using var broker = new ScriptedBroker();
        var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        var second = broker.Process.QueryVolumeAsync('G', CancellationToken.None);
        var firstRequest = await broker.ReadRequestAsync();
        await broker.ReadRequestAsync();
        await broker.WriteControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, firstRequest.RequestId,
            8_000_000, 1024, 8_192_000_000));
        await broker.CloseControlAsync();

        var volume = await first.WaitAsync(HangGuard);
        var exception = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => second.WaitAsync(HangGuard));

        Assert.AreEqual(8_000_000L, volume.MftRecordCount);
        Assert.AreEqual(1024U, volume.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, volume.MftValidDataLength);
        Assert.IsNull(exception.DriveLetter);
    }
}
