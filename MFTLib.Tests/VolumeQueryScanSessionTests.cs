using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class VolumeQueryScanSessionTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task RescanAsync_PlansBlocksOnTheSameBroker()
    {
        var volumeInformation = new NtfsVolumeInformation(100_000 * 1024, 1024, 0, 0, 0, 0);
        await using var harness = new InProcessBlockBrokerHarness(volumeInformation: volumeInformation);
        await using var session = await JournalBrokerScanSession.StartFromCursorsAsync(harness.ConnectAsync,
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(71, 12345) }, BrokerScanProfile.Full);

        await session.RescanAsync(CreateOptions(), harness.CancellationToken);

        using var block = session.LatestScan!.BlockOutcomes["C"].Block;
        var planned = MftBlockCapacity.Plan(volumeInformation);
        Assert.AreEqual(planned.SlotCapacity, block.Header.SlotCapacity);
        Assert.AreEqual(planned.NamePoolCapacity, block.Header.NamePoolCapacity);
        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(0, session.LatestScan.Errors.Count);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledDuringQueryVolumes_DisposesClientAndSession()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var server = serverSide;
        var client = new JournalBrokerClient(clientSide,
            (_, _) => throw new AssertFailedException("Block creation must follow the volume query"));
        await using var session = await JournalBrokerScanSession.StartFromCursorsAsync(_ => Task.FromResult(client),
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(71, 12345) }, BrokerScanProfile.Full);
        using var cancellation = new CancellationTokenSource();
        var scan = session.RescanAsync(CreateOptions(), cancellation.Token);
        var header = new byte[4];
        await server.ReadExactlyAsync(header);
        var bytes = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header)];
        await server.ReadExactlyAsync(bytes);
        Assert.AreEqual((byte)BrokerFrameKind.QueryVolumes, bytes[0]);

        await cancellation.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => scan);

        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => session.RescanAsync(CreateOptions()));
    }
}
