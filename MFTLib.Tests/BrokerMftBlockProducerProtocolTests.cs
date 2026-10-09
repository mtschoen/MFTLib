using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerMftBlockProducerProtocolTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task Produce_PreservesMaximumSkippedRecordCount()
    {
        await using var broker = new ScriptedBroker();
        var resultTask = ProduceAsync(broker.Process, Request(Target()));

        await SendCompleteScanAsync(broker, int.MaxValue);
        var result = await resultTask.WaitAsync(HangGuard);
        result.Block.Dispose();

        Assert.AreEqual(int.MaxValue, result.SkippedRecordCount);
    }

    [TestMethod]
    public async Task Produce_SkippedRecordCountOverflowThrowsAndDisposesBlock()
    {
        await using var broker = new ScriptedBroker();
        var request = Request(Target());
        var resultTask = ProduceAsync(broker.Process, request);

        await SendCompleteScanAsync(broker, (long)int.MaxValue + 1);
        await Assert.ThrowsExceptionAsync<OverflowException>(() => resultTask.WaitAsync(HangGuard));

        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Scan_SendsBlockSpecificationAndPlansFromVolumeQuery()
    {
        await using var broker = new ScriptedBroker();
        var request = Request(Target());
        var resultTask = ProduceAsync(broker.Process, request);

        var (pipe, order) = await broker.AcceptScanAsync(VolumeInformation);
        var section = broker.Sections.Single();

        Assert.AreEqual(BrokerFrameKind.ArmAndScan, order.Kind);
        Assert.AreEqual(section.SectionName, order.RequireSectionName());
        Assert.IsNull(order.DirectoryScanFileNames);
        var expected = MftBlockCapacity.Plan(VolumeInformation);
        Assert.AreEqual(expected.SlotCapacity, section.Block.Header.SlotCapacity);
        Assert.AreEqual(expected.NamePoolCapacity, section.Block.Header.NamePoolCapacity);
        Assert.AreEqual(request.BlockPath, section.Block.Path);

        await CompleteScanAsync(broker, pipe, section.SectionName, 0);
        await pipe.DisposeAsync();
        var result = await resultTask.WaitAsync(HangGuard);
        using var block = result.Block;

        Assert.AreSame(section.Block, block);
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
    }

    [TestMethod]
    public async Task Scan_VolumeQueryFailure_FailsBeforeCreatingBlock()
    {
        // The block is sized from the volume query, so a refused query fails the scan before any
        // section exists (BrokerProcess.Scan.cs: ScanDriveAsync awaits QueryVolumeAsync first).
        await using var broker = new ScriptedBroker();
        var request = Request(Target());
        var resultTask = ProduceAsync(broker.Process, request);

        var query = await broker.ReadRequestAsync();
        Assert.AreEqual(BrokerFrameKind.QueryVolume, query.Kind);
        await broker.WriteControlAsync(writer => BrokerProtocol.WriteError(writer, query.RequestId, "query failed"));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => resultTask.WaitAsync(HangGuard));

        Assert.AreEqual("query failed", exception.Message);
        Assert.AreEqual(0, broker.Sections.All().Count);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Produce_DisconnectionDisposesBlockBeforeReturning(bool sendReady)
    {
        await using var broker = new ScriptedBroker();
        var request = Request(Target());
        var resultTask = ProduceAsync(broker.Process, request);

        var (pipe, _) = await broker.AcceptScanAsync(VolumeInformation);
        await using (pipe)
        {
            if (sendReady)
            {
                broker.WriteSection(broker.Sections.Single().SectionName, ArmedCursor);
                await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, ArmedCursor));
                await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0));
            }
        }

        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => resultTask.WaitAsync(HangGuard));
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    [DataRow("MissingReady", "ScanProgress or ScanReady")]
    [DataRow("MissingCursor", "Cursor")]
    [DataRow("RepeatedReady", "ScanCompleted or CatchUpLost")]
    [DataRow("ErrorAfterReady", "scan failed after ready")]
    public async Task Produce_RejectsBrokenExchangeAndDisposesBlock(string fault, string message)
    {
        await using var broker = new ScriptedBroker();
        var resultTask = ProduceAsync(broker.Process, Request(Target()));

        var (pipe, _) = await broker.AcceptScanAsync(VolumeInformation);
        await using (pipe)
        {
            broker.WriteSection(broker.Sections.Single().SectionName, ArmedCursor);
            if (fault != "MissingCursor")
            {
                await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, ArmedCursor));
            }

            if (fault == "MissingReady")
            {
                await HostChannelHarness.WriteFrameAsync(pipe,
                    writer => BrokerProtocol.WriteScanCompleted(writer, ArmedCursor));
            }
            else
            {
                await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0));
            }

            if (fault == "RepeatedReady")
            {
                await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0));
            }
            else if (fault == "ErrorAfterReady")
            {
                await HostChannelHarness.WriteFrameAsync(pipe,
                    writer => BrokerProtocol.WriteError(writer, 0, "scan failed after ready"));
            }
        }

        Exception exception;
        if (fault == "ErrorAfterReady")
        {
            exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => resultTask.WaitAsync(HangGuard));
        }
        else
        {
            exception = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => resultTask.WaitAsync(HangGuard));
        }

        StringAssert.Contains(exception.Message, message);
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
    }

    static async Task SendCompleteScanAsync(ScriptedBroker broker, long skippedRecordCount)
    {
        var (pipe, _) = await broker.AcceptScanAsync(VolumeInformation);
        await using var channel = pipe;
        await CompleteScanAsync(broker, channel, broker.Sections.Single().SectionName, skippedRecordCount);
    }

    static async Task CompleteScanAsync(ScriptedBroker broker, Stream pipe, string sectionName, long skippedRecordCount)
    {
        broker.WriteSection(sectionName, ArmedCursor);
        await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, ArmedCursor));
        await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, skippedRecordCount));
        await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanCompleted(writer, ArmedCursor));
    }
}
