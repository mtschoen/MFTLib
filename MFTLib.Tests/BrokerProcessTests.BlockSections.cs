using MFTLib.Index;
using MFTLibTestExtensions;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task ScanDriveAsync_ScanReady_ReleasesThatDrivesSectionWhileAnUnreadDriveStaysLive()
    {
        var armedC = new UsnJournalCursor(7, 100);
        var advancedC = new UsnJournalCursor(7, 110);
        var armedD = new UsnJournalCursor(8, 200);
        var advancedD = new UsnJournalCursor(8, 210);
        await using var broker = new ScriptedBroker();

        var scanC = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(), CancellationToken.None);
        var (pipeC, _) = await broker.AcceptScanAsync(Volume);
        var channelC = pipeC;
        var sectionC = broker.Sections.ForDrive('C');
        broker.WriteSection(sectionC.SectionName, armedC);
        await HostChannelHarness.WriteFrameAsync(channelC, writer => BrokerProtocol.WriteCursor(writer, armedC));
        await HostChannelHarness.WriteFrameAsync(channelC, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await sectionC.Lifetime.Disposed.WaitAsync(HangGuard);

        // C's section is released after its ScanReady while its scan still waits for catch-up.
        var scanD = broker.Process.ScanDriveAsync('D', TestBlockSections.Target(), new BrokerScanOptions(), CancellationToken.None);
        var (pipeD, _) = await broker.AcceptScanAsync(Volume);
        var channelD = pipeD;
        var sectionD = broker.Sections.ForDrive('D');

        Assert.AreEqual(1, sectionC.Lifetime.DisposeCount, "Drive C's section must be released once its ScanReady is consumed");
        Assert.AreEqual(0, sectionD.Lifetime.DisposeCount, "Drive D's section must stay live while its scan is unread");
        Assert.IsFalse(scanC.IsCompleted);

        await HostChannelHarness.WriteFrameAsync(channelC, writer => BrokerProtocol.WriteScanCompleted(writer, advancedC));
        await channelC.DisposeAsync();
        var resultC = await scanC.WaitAsync(HangGuard);
        Assert.AreEqual(0, sectionD.Lifetime.DisposeCount, "Drive C completing must not release drive D's section");

        broker.WriteSection(sectionD.SectionName, armedD);
        await HostChannelHarness.WriteFrameAsync(channelD, writer => BrokerProtocol.WriteCursor(writer, armedD));
        await HostChannelHarness.WriteFrameAsync(channelD, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await HostChannelHarness.WriteFrameAsync(channelD, writer => BrokerProtocol.WriteScanCompleted(writer, advancedD));
        await channelD.DisposeAsync();
        var resultD = await scanD.WaitAsync(HangGuard);
        using var blockC = resultC.Block.Block;
        using var blockD = resultD.Block.Block;

        Assert.AreEqual(1, sectionC.Lifetime.DisposeCount);
        Assert.AreEqual(1, sectionD.Lifetime.DisposeCount);
        Assert.AreEqual(armedC, resultC.ArmedCursor);
        Assert.AreEqual(advancedC, resultC.AdvancedCursor);
        Assert.AreEqual(armedD, resultD.ArmedCursor);
        Assert.AreEqual(advancedD, resultD.AdvancedCursor);
        Assert.AreSame(sectionC.Block, blockC);
        Assert.AreSame(sectionD.Block, blockD);
    }

    [TestMethod]
    public async Task ScanDriveAsync_ErrorFrame_DisposesSectionLifetimeImmediately()
    {
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _, _) =>
            throw new IOException("drive failed")));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => broker.Process.ScanDriveAsync('C',
            TestBlockSections.Target(), new BrokerScanOptions(), CancellationToken.None).WaitAsync(HangGuard));

        var section = broker.Sections.Single();
        Assert.AreEqual("drive failed", exception.Message);
        Assert.AreEqual(1, section.Lifetime.DisposeCount, "an Error frame must immediately release the failed drive's section");
        BlockFileAssertions.IsDisposed(section.Block);
    }

    [TestMethod]
    public async Task ScanDriveAsync_ScanProgressFrames_ReachTheOptionsProgressCallback()
    {
        var reports = new List<BrokerScanProgress>();
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions
        {
            Progress = new SynchronousProgress<BrokerScanProgress>(reports.Add)
        }, CancellationToken.None);

        var (pipe, _) = await broker.AcceptScanAsync(Volume);
        var channel = pipe;
        broker.WriteSection(broker.Sections.Single().SectionName, Armed);
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteCursor(writer, Armed));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanProgress(writer,
            new BrokerScanProgress
            {
                DriveLetter = "C",
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 50,
                BytesProcessed = 1000,
                TotalRecords = 100,
                TotalBytes = 2000,
                Elapsed = TimeSpan.FromMilliseconds(50)
            }));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanProgress(writer,
            new BrokerScanProgress
            {
                DriveLetter = "C",
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 100,
                BytesProcessed = 2000,
                TotalRecords = 100,
                TotalBytes = 2000,
                Elapsed = TimeSpan.FromMilliseconds(100)
            }));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanCompleted(writer, Armed));
        await channel.DisposeAsync();
        var result = await scan.WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual(2, reports.Count);
        Assert.AreEqual(50, reports[0].RecordsProcessed);
        Assert.AreEqual(1000, reports[0].BytesProcessed);
        Assert.AreEqual(100, reports[1].RecordsProcessed);
        Assert.AreEqual(2000, reports[1].BytesProcessed);
        Assert.IsTrue(reports.All(report => report.TotalRecords == 100 && report.TotalBytes == 2000));
        Assert.IsTrue(reports.All(report => report.DriveLetter == "C"),
            "the client fills the drive letter in from the channel a progress frame arrived on");
    }

    [TestMethod]
    public async Task ScanDriveAsync_ScanProgressFrame_DoesNotCompleteTheScan()
    {
        var progressReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<BrokerScanProgress>();
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions
        {
            Progress = new SynchronousProgress<BrokerScanProgress>(report =>
            {
                reports.Add(report);
                progressReceived.TrySetResult();
            })
        }, CancellationToken.None);

        var (pipe, _) = await broker.AcceptScanAsync(Volume);
        var channel = pipe;
        var section = broker.Sections.Single();
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteCursor(writer, Armed));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanProgress(writer,
            new BrokerScanProgress
            {
                DriveLetter = "C",
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 10,
                BytesProcessed = 200,
                TotalRecords = 100,
                TotalBytes = 2000,
                Elapsed = TimeSpan.FromMilliseconds(10)
            }));
        await progressReceived.Task.WaitAsync(HangGuard);

        Assert.IsFalse(scan.IsCompleted, "a ScanProgress frame must not complete the drive");
        Assert.AreEqual(0, section.Lifetime.DisposeCount, "the section is released by ScanReady, not by progress");

        var advanced = new UsnJournalCursor(7, 1200);
        broker.WriteSection(section.SectionName, Armed);
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanReady(writer, 0));
        await HostChannelHarness.WriteFrameAsync(channel, writer => BrokerProtocol.WriteScanCompleted(writer, advanced));
        await channel.DisposeAsync();
        var result = await scan.WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(10, reports[0].RecordsProcessed);
        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.AreEqual(advanced, result.AdvancedCursor);
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
    }

    [TestMethod]
    public async Task ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased()
    {
        var sections = new ThrowingLifetimeSections();
        using var writer = new RecordingBlockSectionWriter(sections.Inner.Resolve);
        await using var handle = BrokerTestHarness.Start(CreateHost(), writer, sections.Create,
            new BrokerTestHarnessOptions(), null, null);

        var exception = await Assert.ThrowsExceptionAsync<IOException>(() => handle.Process.ScanDriveAsync('C',
            TestBlockSections.Target(), new BrokerScanOptions(), CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual("release failed", exception.Message);
        Assert.AreEqual(1, sections.Lifetime.DisposeCount, "the catch must not dispose a lifetime whose release already ran");
        BlockFileAssertions.IsDisposed(sections.Inner.Single().Block);
        sections.Inner.Dispose();
    }

    sealed class ThrowingLifetimeSections
    {
        public TestBlockSections Inner { get; } = new();

        public ThrowingLifetime Lifetime { get; } = new();

        public (string SectionName, BlockFile Block, IDisposable Lifetime) Create(char driveLetter,
            BlockFileCreateOptions options)
        {
            var (sectionName, block, _) = Inner.Create(driveLetter, options);
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
