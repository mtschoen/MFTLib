using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerBlockContractTests
{
    [TestMethod]
    public async Task Scan_WithoutTargetsRejectsBeforeWritingAnyFrame()
    {
        using var stream = new MemoryStream();
        await using var client = new JournalBrokerClient(stream,
            (_, _) => throw new AssertFailedException("No section should be created"));

        // Options are now required, so the only way to reach the scan without a
        // destination for a requested drive is to supply options that carry none.
        var exception = await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            client.ArmScanAndCatchUpAsync(["C"], new BrokerScanOptions()));

        StringAssert.Contains(exception.Message, "C");
        Assert.AreEqual(0L, stream.Length);
    }

    [TestMethod]
    public void ScanReady_RoundTripsBlockCounts()
    {
        var expected = BrokerFrame.ScanReady("section-C", 4_000_000_000L, 5_000_000_000L, 3_000_000_000L);
        var writer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteScanReady(writer, expected.MmfName!, expected.RowCount,
            expected.NamePoolUsedBytes, expected.SkippedRecordCount);

        var actual = BrokerProtocol.ReadFrame(writer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.ScanReady, actual.Kind);
        Assert.AreEqual(expected.MmfName, actual.MmfName);
        Assert.AreEqual(expected.RowCount, actual.RowCount);
        Assert.AreEqual(expected.NamePoolUsedBytes, actual.NamePoolUsedBytes);
        Assert.AreEqual(expected.SkippedRecordCount, actual.SkippedRecordCount);
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
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, _, _) =>
        [[new MftRecord(5, 5, new MftRecordFields(3), ".", null),
            new MftRecord(20, 5, new MftRecordFields(1), string.Empty, null)]]);
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync).CreateProducer();

        var result = await producer(harness.Request, harness.CancellationToken);
        using var block = result.Block;

        Assert.AreEqual(1, result.SkippedRecordCount);
    }
}
