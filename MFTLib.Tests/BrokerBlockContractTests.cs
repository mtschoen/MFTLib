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
            broker.Process.ScanDriveAsync('C', null!, new MftScanOptions(), null, CancellationToken.None).WaitAsync(HangGuard));
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.IsEmpty(broker.Sections.All());
        await Assert.ThrowsExactlyAsync<AssertFailedException>(broker.ReadRequestAsync,
            "no request may reach the control pipe before the client closed it");
    }

    [TestMethod]
    public async Task Scan_NormalizesTheDriveLetter()
    {
        var drives = new List<DriveKey>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = drive =>
            {
                drives.Add(drive);
                return VolumeInformation;
            }
        }));

        var result = await broker.Process.ScanDriveAsync('c', Target(), new MftScanOptions(), null, CancellationToken.None)
            .WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual(new DriveKey('C'), result.Drive);
        Assert.AreSequenceEqual([new DriveKey('C')], drives);
        Assert.StartsWith("section-C-", broker.Sections.Single().SectionName);
    }

    [TestMethod]
    public void ScanReady_RoundTripsSkippedRecordCountBeyondInt32()
    {
        var writer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteScanReady(writer, 3_000_000_000L);

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
            ScanDrive = (_, _, _, _, _, _) =>
            [
                [BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(20, string.Empty)]
            ]
        }));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard);
        using var block = result.Block;

        Assert.AreEqual(1, result.SkippedRecordCount);
    }
}
