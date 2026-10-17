using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task ScanDriveAsync_ScanReady_ReleasesThatDrivesSectionWhileAnUnreadDriveStaysLive()
    {
        var armedC = new UsnJournalCursor(7, 100);
        var armedD = new UsnJournalCursor(8, 200);
        await using var broker = new ScriptedBroker();

        var scanC = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(), null, CancellationToken.None);
        var (pipeC, _) = await broker.AcceptScanAsync(Volume);
        var channelC = pipeC;
        var sectionC = broker.Sections.ForDrive('C');
        broker.WriteSection(sectionC.SectionName, armedC);
        await HostChannelHarness.WriteFrameAsync(channelC, writer => BrokerProtocol.WriteCursor(writer, armedC));
        await HostChannelHarness.WriteFrameAsync(channelC, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await sectionC.Lifetime.Disposed.WaitAsync(HangGuard);

        // C's section is released after its ScanReady while its scan still waits for catch-up.
        var scanD = broker.Process.ScanDriveAsync('D', TestBlockSections.Target(), new MftScanOptions(), null, CancellationToken.None);
        var (pipeD, _) = await broker.AcceptScanAsync(Volume);
        var channelD = pipeD;
        var sectionD = broker.Sections.ForDrive('D');

        Assert.AreEqual(1, sectionC.Lifetime.DisposeCount, "Drive C's section must be released once its ScanReady is consumed");
        Assert.AreEqual(0, sectionD.Lifetime.DisposeCount, "Drive D's section must stay live while its scan is unread");
        Assert.IsFalse(scanC.IsCompleted);

        await HostChannelHarness.WriteFrameAsync(channelC, BrokerProtocol.WriteScanCompleted);
        await channelC.DisposeAsync();
        var resultC = await scanC.WaitAsync(HangGuard);
        Assert.AreEqual(0, sectionD.Lifetime.DisposeCount, "Drive C completing must not release drive D's section");

        broker.WriteSection(sectionD.SectionName, armedD);
        await HostChannelHarness.WriteFrameAsync(channelD, writer => BrokerProtocol.WriteCursor(writer, armedD));
        await HostChannelHarness.WriteFrameAsync(channelD, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await HostChannelHarness.WriteFrameAsync(channelD, BrokerProtocol.WriteScanCompleted);
        await channelD.DisposeAsync();
        var resultD = await scanD.WaitAsync(HangGuard);
        using var blockC = resultC.Block.Block;
        using var blockD = resultD.Block.Block;

        Assert.AreEqual(1, sectionC.Lifetime.DisposeCount);
        Assert.AreEqual(1, sectionD.Lifetime.DisposeCount);
        Assert.AreEqual(armedC, resultC.ArmedCursor);
        Assert.IsNull(resultC.CatchUpLoss);
        Assert.AreEqual(armedD, resultD.ArmedCursor);
        Assert.IsNull(resultD.CatchUpLoss);
        Assert.AreSame(sectionC.Block, blockC);
        Assert.AreSame(sectionD.Block, blockD);
    }

    [TestMethod]
    public async Task ScanDriveAsync_ErrorFrame_DisposesSectionLifetimeImmediately()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) =>
                throw new IOException("drive failed")
        }));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => broker.Process.ScanDriveAsync('C',
            TestBlockSections.Target(), new MftScanOptions(), null, CancellationToken.None).WaitAsync(HangGuard));

        var section = broker.Sections.Single();
        Assert.AreEqual("drive failed", exception.Message);
        Assert.AreEqual(1, section.Lifetime.DisposeCount, "an Error frame must immediately release the failed drive's section");
        BlockFileAssertions.IsDisposed(section.Block);
    }

    [TestMethod]
    public async Task ScanDriveAsync_ScanProgressFrames_ReachTheOptionsProgressCallback()
    {
        var reports = new List<BlockWriteProgress>();
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            new SynchronousProgress<BlockWriteProgress>(reports.Add), CancellationToken.None);

        var (pipe, _) = await broker.AcceptScanAsync(Volume);
        var channel = pipe;
        broker.WriteSection(broker.Sections.Single().SectionName, Armed);
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteCursor(writer, Armed));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanProgress(writer,
            new BrokerScanProgress
            {
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 50,
                BytesProcessed = 1000,
                TotalRecords = 100,
                TotalBytes = 2000
            }));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanProgress(writer,
            new BrokerScanProgress
            {
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 100,
                BytesProcessed = 2000,
                TotalRecords = 100,
                TotalBytes = 2000
            }));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await HostChannelHarness.WriteFrameAsync(channel, BrokerProtocol.WriteScanCompleted);
        await channel.DisposeAsync();
        var result = await scan.WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual(2, reports.Count);
        Assert.AreEqual(50, reports[0].RecordsProcessed);
        Assert.AreEqual(1000, reports[0].BytesProcessed);
        Assert.AreEqual(100, reports[1].RecordsProcessed);
        Assert.AreEqual(2000, reports[1].BytesProcessed);
        Assert.IsTrue(reports.All(report => report.TotalRecords == 100 && report.TotalBytes == 2000));
        Assert.IsTrue(reports.All(report => report.Phase == BlockScanPhase.Parsing));
    }

    [TestMethod]
    public async Task ScanDriveAsync_ScanProgressFrame_DoesNotCompleteTheScan()
    {
        var progressReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<BlockWriteProgress>();
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            new SynchronousProgress<BlockWriteProgress>(report =>
            {
                reports.Add(report);
                progressReceived.TrySetResult();
            }), CancellationToken.None);

        var (pipe, _) = await broker.AcceptScanAsync(Volume);
        var channel = pipe;
        var section = broker.Sections.Single();
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteCursor(writer, Armed));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanProgress(writer,
            new BrokerScanProgress
            {
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 10,
                BytesProcessed = 200,
                TotalRecords = 100,
                TotalBytes = 2000
            }));
        await progressReceived.Task.WaitAsync(HangGuard);

        Assert.IsFalse(scan.IsCompleted, "a ScanProgress frame must not complete the drive");
        Assert.AreEqual(0, section.Lifetime.DisposeCount, "the section is released by ScanReady, not by progress");

        broker.WriteSection(section.SectionName, Armed);
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await HostChannelHarness.WriteFrameAsync(channel, BrokerProtocol.WriteScanCompleted);
        await channel.DisposeAsync();
        var result = await scan.WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(10, reports[0].RecordsProcessed);
        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.IsNull(result.CatchUpLoss);
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
    }

    [TestMethod]
    public async Task ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased()
    {
        var sections = new ThrowingLifetimeSections();
        var writer = new RecordingBlockSectionWriter(sections.Inner.Resolve);
        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to InProcessBrokerHandle on Start
        var resources = new ScriptedBrokerResources(sections.Inner, writer, sections.Create);
        await using var handle = BrokerTestHarness.Start(BrokerHostFactory.Create(), resources,
            new BrokerTestHarnessOptions(), null, null);

        var exception = await Assert.ThrowsExceptionAsync<IOException>(() => handle.Process.ScanDriveAsync('C',
            TestBlockSections.Target(), new MftScanOptions(), null, CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual("release failed", exception.Message);
        Assert.AreEqual(1, sections.Lifetime.DisposeCount, "the catch must not dispose a lifetime whose release already ran");
        BlockFileAssertions.IsDisposed(sections.Inner.Single().Block);
    }

    sealed class ThrowingLifetimeSections
    {
        public TestBlockSections Inner { get; } = new();

        public ThrowingLifetime Lifetime { get; } = new();

        public (string SectionName, BlockFile Block, IDisposable Lifetime) Create(DriveKey drive,
            BlockFileCreateOptions options)
        {
            var (sectionName, block, _) = Inner.Create(drive, options);
            return (sectionName, block, Lifetime);
        }
    }

    sealed class ThrowingLifetime : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            throw new IOException("release failed");
        }
    }
}
