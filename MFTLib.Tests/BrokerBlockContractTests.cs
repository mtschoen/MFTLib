using System.Buffers;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerBlockContractTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task Scan_WithoutTargetRejectsBeforeWritingAnyFrame()
    {
        await using var broker = new ScriptedBroker();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            broker.Process.ScanVolumeAsync('C', null!, new MftScanOptions(), null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsEmpty(broker.Sections.All());
        await Assert.ThrowsExactlyAsync<AssertFailedException>(broker.ReadRequestAsync,
            "no request may reach the control pipe before the client closed it");
    }

    [TestMethod]
    public async Task Scan_NormalizesTheDriveLetter()
    {
        var volumes = new List<VolumeKey>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = volume =>
            {
                volumes.Add(volume);
                return VolumeInformation;
            }
        }));

        var result = await broker.Process.ScanVolumeAsync('c', Target(), new MftScanOptions(), null, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        result.Block.Block.Dispose();

        Assert.AreEqual(new VolumeKey('C'), result.Volume);
        Assert.AreSequenceEqual([new VolumeKey('C')], volumes);
        Assert.StartsWith("mftlib-block-C-", broker.Sections.Single().SectionName);
    }

    [TestMethod]
    public void ScanReady_RoundTripsSkippedRecordCountBeyondInt32()
    {
        var writer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteScanReady(writer, BrokerFrame.ScanReady(3_000_000_000L));

        var actual = BrokerProtocol.ReadFrame(writer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.ScanReady, actual.Kind);
        Assert.AreEqual(3_000_000_000L, actual.SkippedRecordCount);
        Assert.AreEqual(writer.WrittenCount, consumed);
    }

    [TestMethod]
    public void ScanPhases_ContainOnlyParsingAndTransferring()
    {
        Assert.AreSequenceEqual([BrokerScanPhase.Parsing, BrokerScanPhase.Transferring], Enum.GetValues<BrokerScanPhase>());
    }

    [TestMethod]
    public async Task Produce_ReportsSkippedRecordsFromTheBlockWriter()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanVolume = (_, _, _, _, _, _) =>
            [
                [BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(20, string.Empty)]
            ]
        }));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.AreEqual(1, result.SkippedRecordCount);
    }

    public TestContext TestContext { get; set; } = null!;
}
