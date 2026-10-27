using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class VolumeQueryClientTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task QueryVolumeAsync_OneRootSucceedsAndOneErrors_ReturnsTheVolumeAndThrowsTheHostMessage()
    {
        var reply = new NtfsVolumeInformation(8_192_000_000, 1024);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = volume => volume == 'C'
                ? reply
                : throw new UnauthorizedAccessException("access denied")
        }));

        var volume = await broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.QueryVolumeAsync('G', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(8_000_000L, volume.MftRecordCount);
        Assert.AreEqual(1024U, volume.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, volume.MftValidDataLength);
        Assert.AreEqual("access denied", exception.Message);
    }

    [TestMethod]
    public async Task QueryVolumeAsync_DefaultRoot_ThrowsArgumentException()
    {
        await using var broker = new ScriptedBroker();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            broker.Process.QueryVolumeAsync(default, CancellationToken.None));
    }

    [TestMethod]
    public async Task QueryVolumeAsync_ControlPipeClosesAfterOneReply_AnsweredQuerySucceedsAndPendingQueryLosesTheProcess()
    {
        await using var broker = new ScriptedBroker();
        var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        var second = broker.Process.QueryVolumeAsync('G', CancellationToken.None);
        var firstRequest = await broker.ReadRequestAsync();
        await broker.ReadRequestAsync();
        await broker.WriteControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, BrokerFrame.VolumeInfo(firstRequest.RequestId,
            1024, 8_192_000_000)));
        await broker.CloseControlAsync();

        var volume = await first.WaitAsync(HangGuard, TestContext.CancellationToken);
        var exception = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => second.WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(8_000_000L, volume.MftRecordCount);
        Assert.AreEqual(1024U, volume.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, volume.MftValidDataLength);
        Assert.IsNull(exception.Volume);
    }

    public TestContext TestContext { get; set; } = null!;
}
