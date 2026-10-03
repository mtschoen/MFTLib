using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerBlockContractTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task Scan_WithoutTargetRejectsBeforeWritingAnyFrame()
    {
        await using var broker = new ScriptedBroker();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            broker.Process.ScanDriveAsync('C', null!, new BrokerScanOptions(), CancellationToken.None).WaitAsync(HangGuard));
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.AreEqual(0, broker.Sections.All().Count);
        await Assert.ThrowsExceptionAsync<AssertFailedException>(() => broker.ReadRequestAsync(),
            "no request may reach the control pipe before the client closed it");
    }

    [TestMethod]
    public async Task Scan_NormalizesTheDriveLetter()
    {
        var drives = new List<string>();
        await using var broker = new InProcessBroker(CreateHost(queryVolumeInfo: drive =>
        {
            drives.Add(drive);
            return VolumeInformation;
        }));

        var result = await broker.Process.ScanDriveAsync('c', Target(), new BrokerScanOptions(), CancellationToken.None)
            .WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual('C', result.DriveLetter);
        CollectionAssert.AreEqual(new[] { "C" }, drives);
        StringAssert.StartsWith(broker.Sections.Single().SectionName, "section-C-");
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
        CollectionAssert.AreEqual(new[] { BrokerScanPhase.Parsing, BrokerScanPhase.Transferring },
            Enum.GetValues<BrokerScanPhase>());
    }

    [TestMethod]
    public async Task Produce_ReportsSkippedRecordsFromTheBlockWriter()
    {
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _) =>
        [
            [Record(5, ".", 3), Record(20, string.Empty)]
        ]));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard);
        using var block = result.Block;

        Assert.AreEqual(1, result.SkippedRecordCount);
    }
}
