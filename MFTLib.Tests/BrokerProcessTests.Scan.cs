using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    // Puts the armed cursor (7, 1000) below the journal's first USN: a proven trim.
    static readonly JournalWindow TrimmedWindow = new(7, 5000, 9000, 4096, 32768);

    // Still holds the armed cursor: a catch-up failure the journal does not prove.
    static readonly JournalWindow RetainedWindow = new(7, 500, 9000, 4096, 32768);

    [TestMethod]
    public async Task ScanDrive_ReturnsArmedAndAdvancedCursorsAndBlock()
    {
        var advanced = new UsnJournalCursor(7, 1500);
        await using var broker = new InProcessBroker(CreateHost(
            readJournal: CatchUpSources.ToTip(advanced, JournalEntries.Create(20, 1200, "file.txt"))));
        var target = TestBlockSections.Target() with { CacheTag = new CacheTag("TEST", 3) };

        var result = await broker.Process.ScanDriveAsync('c', target, new BrokerScanOptions(), CancellationToken.None)
            .WaitAsync(HangGuard);
        using var block = result.Block.Block;

        var (_, sectionBlock, lifetime) = broker.Sections.Single();
        Assert.AreEqual('C', result.DriveLetter);
        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.AreEqual(advanced, result.AdvancedCursor);
        Assert.IsNull(result.CatchUpLoss);
        Assert.AreSame(sectionBlock, block);
        Assert.IsTrue(block.Header.RowCount > 0);
        Assert.AreEqual(Armed.JournalId, block.Header.UsnJournalId);
        Assert.AreEqual(target.CacheTag, block.Header.CacheTag);
        Assert.AreEqual(target.Path, block.Path);
        Assert.IsTrue(lifetime.IsDisposed, "The section name is unpublished once the block is written.");
    }

    [TestMethod]
    public async Task ScanDrive_ReportsProgress()
    {
        var reports = new List<BrokerScanProgress>();
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, progress, _, _) =>
        {
            progress?.Report(new BlockWriteProgress(2, 0, 2, null, BrokerScanPhase.Parsing));
            return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
        }));
        var options = new BrokerScanOptions
        {
            Progress = new SynchronousProgress<BrokerScanProgress>(report =>
            {
                lock (reports)
                {
                    reports.Add(report);
                }
            })
        };

        var result = await broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), options, CancellationToken.None)
            .WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.IsTrue(reports.Count > 0);
        Assert.IsTrue(reports.All(report => report.DriveLetter == "C"), "The channel's drive fills the sample.");
        Assert.AreEqual(BrokerScanPhase.Transferring, reports[^1].Phase);
    }

    [TestMethod]
    public async Task ScanDrive_ForwardsProfileAndKeepFileNames()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var options = new BrokerScanOptions
        {
            Profile = BrokerScanProfile.DirectoryIndex,
            KeepFileNames = [".git"]
        };

        var result = await broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), options, CancellationToken.None)
            .WaitAsync(HangGuard);
        result.Block.Block.Dispose();

        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, broker.Writer.LastFilter.Profile);
        CollectionAssert.AreEqual(new[] { ".git" }, broker.Writer.LastFilter.KeepFileNames!.ToArray());
    }

    [TestMethod]
    public async Task ScanDrive_CatchUpLost_ReturnsBlockWithLossAndArmedCursor()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => TrimmedWindow);
        var hostEnd = new TaskCompletionSource<DisposalRecordingStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(
            CreateHost(readJournal: (_, _, _) => throw new IOException("catch-up read failed")),
            wrapConnector: RecordHostEnd(hostEnd));

        var result = await broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None).WaitAsync(HangGuard);
        using var block = result.Block.Block;

        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.IsNull(result.AdvancedCursor);
        var loss = result.CatchUpLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual('C', loss.DriveLetter);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(Armed.NextUsn, loss.CheckpointUsn);
        Assert.AreEqual(5000L, loss.FirstUsn);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
        Assert.IsTrue(block.Header.RowCount > 0, "The section stays alive for the caller.");
        await (await hostEnd.Task.WaitAsync(HangGuard)).Disposed.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task ScanDrive_ErrorAfterScanReady_DisposesSectionAndReturnsNoBlock()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => RetainedWindow);
        await using var broker = new InProcessBroker(
            CreateHost(readJournal: (_, _, _) => throw new IOException("catch-up read failed")));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
                CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual("catch-up read failed", exception.Message);
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task ScanDrive_CatchUpLostBeforeScanReady_IsProtocolError()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, Armed)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteCatchUpLost(writer, TrimmedLoss())));

        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "CatchUpLost");
    }

    [TestMethod]
    public async Task ScanDrive_ScanCompletedAfterCatchUpLost_IsProtocolError()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, Armed)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteCatchUpLost(writer, TrimmedLoss())),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteScanCompleted(writer, Armed)));

        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "ScanCompleted");
    }

    [TestMethod]
    public async Task ScanDrive_JournalBatchAsTerminalFrame_IsProtocolError()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, Armed)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteJournalBatch(writer, Armed, [])));

        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "JournalBatch");
        StringAssert.Contains(lost.Message, "ScanCompleted or CatchUpLost");
    }

    // After its terminal frame the host only closes the channel. A frame the host starts and never
    // finishes is a lost channel, not the EOF that completes the scan.
    [TestMethod]
    public async Task ScanDrive_TruncatedFrameAfterTerminal_IsChannelLost()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, Armed)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteScanCompleted(writer, Armed)),
            pipe => WriteRawAsync(pipe, [10, 0, 0, 0, (byte)BrokerFrameKind.Heartbeat]));

        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "Truncated");
    }

    [TestMethod]
    public async Task ScanDrive_InvalidFrameLengthAfterTerminal_IsChannelLost()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, Armed)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, 0)),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteScanCompleted(writer, Armed)),
            pipe => WriteRawAsync(pipe, [0, 0, 0, 0]));

        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "too short");
    }

    [TestMethod]
    public async Task Producer_CatchUpLost_CopiesLossToProduceResult()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => TrimmedWindow);
        await using var broker = new InProcessBroker(
            CreateHost(readJournal: (_, _, _) => throw new IOException("catch-up read failed")));
        var process = broker.Process;
        var producer = new BrokerMftBlockProducer(_ => Task.FromResult(process)).CreateIndexSource().Producer;
        var target = TestBlockSections.Target();

        var produced = await producer(new MftBlockProduceRequest
        {
            DriveLetter = 'C',
            VolumeSerial = target.VolumeSerial,
            BlockPath = target.Path,
            DeleteOnClose = true
        }, CancellationToken.None).WaitAsync(HangGuard);
        using var block = produced.Block;

        Assert.IsNotNull(produced.CatchUpLoss);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, produced.CatchUpLoss.DetectedDuring);
        Assert.AreEqual(5000L, produced.CatchUpLoss.FirstUsn);
        Assert.AreEqual(Armed.JournalId, produced.JournalId);
        Assert.AreEqual(Armed.NextUsn, produced.NextUsn);
        Assert.IsTrue(block.Header.RowCount > 0);
    }

    [TestMethod]
    public async Task ScanDrive_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(
            scanDrive: (_, _, _, _, _, cancellationToken) => WaitForCancellation(scanning, sourceCancelled, cancellationToken)));
        using var cancellation = new CancellationTokenSource();

        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            cancellation.Token);
        await scanning.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();

        await AssertCancelledAsync(scan);
        await sourceCancelled.Task.WaitAsync(HangGuard);
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task ScanDrive_CancelOne_OtherDriveCompletes()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (drive, _, _, _, _, cancellationToken) =>
            drive == "C"
                ? WaitForCancellation(scanning, sourceCancelled, cancellationToken)
                : [[Record(5, ".", 3)], [Record(20, "file.txt")]]));
        using var cancellation = new CancellationTokenSource();

        var cancelled = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            cancellation.Token);
        await scanning.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();
        await AssertCancelledAsync(cancelled);
        await sourceCancelled.Task.WaitAsync(HangGuard);
        var other = await broker.Process.ScanDriveAsync('D', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None).WaitAsync(HangGuard);
        using var block = other.Block.Block;

        Assert.AreEqual('D', other.DriveLetter);
        Assert.IsTrue(block.Header.RowCount > 0);
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task ScanDrive_TwoDrivesConcurrently_BothComplete()
    {
        var gates = new Dictionary<string, TestGate> { ["C"] = new(), ["D"] = new() };
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (drive, _, _, _, _, _) =>
        {
            gates[drive].MarkEntered();
            gates[drive].WaitForRelease();
            return [[Record(5, ".", 3)], [Record(20, drive + ".txt")]];
        }));

        var scanC = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        var scanD = broker.Process.ScanDriveAsync('D', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await Task.WhenAll(gates["C"].Entered, gates["D"].Entered).WaitAsync(HangGuard);
        gates["C"].Release();
        gates["D"].Release();
        var results = await Task.WhenAll(scanC, scanD).WaitAsync(HangGuard);

        foreach (var result in results)
        {
            using var block = result.Block.Block;
            Assert.IsTrue(block.Header.RowCount > 0);
        }

        CollectionAssert.AreEqual(new[] { 'C', 'D' }, results.Select(result => result.DriveLetter).ToArray());
    }

    [TestMethod]
    public async Task Producer_ReportsProgressToRequestAndScanOptions()
    {
        var indexReports = new List<IndexScanProgress>();
        var brokerReports = new List<BrokerScanProgress>();
        await using var broker = new InProcessBroker(CreateHost());
        var process = broker.Process;
        var producer = new BrokerMftBlockProducer(_ => Task.FromResult(process), new BrokerScanOptions
        {
            Progress = new SynchronousProgress<BrokerScanProgress>(brokerReports.Add)
        }).CreateIndexSource().Producer;
        var target = TestBlockSections.Target();

        var produced = await producer(new MftBlockProduceRequest
        {
            DriveLetter = 'C',
            VolumeSerial = target.VolumeSerial,
            BlockPath = target.Path,
            DeleteOnClose = true,
            Progress = new SynchronousProgress<IndexScanProgress>(indexReports.Add)
        }, CancellationToken.None).WaitAsync(HangGuard);
        produced.Block.Dispose();

        Assert.IsNull(produced.CatchUpLoss);
        Assert.AreEqual(brokerReports.Count, indexReports.Count);
        Assert.IsTrue(indexReports.Count > 0);
        Assert.IsTrue(indexReports.All(report => report.DriveLetter == 'C'));
        Assert.AreEqual(IndexScanPhase.Transferring, indexReports[^1].Phase);
        Assert.AreEqual((uint?)brokerReports[^1].TotalRecords, indexReports[^1].TotalRows);
    }

    [TestMethod]
    public async Task ScanDrive_ProcessEndsBeforeChannelOpens_FailsNamingDrive()
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);

        await broker.CloseControlAsync();

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
    }

    // Blocks the host's scan until its channel is cancelled, then reports that it saw the cancellation.
    static IEnumerable<IReadOnlyList<MftRecord>> WaitForCancellation(TestGate scanning, TaskCompletionSource cancelled,
        CancellationToken cancellationToken)
    {
        scanning.MarkEntered();
        cancellationToken.WaitHandle.WaitOne(HangGuard);
        if (cancellationToken.IsCancellationRequested)
        {
            cancelled.TrySetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [];
    }

    // The distinct case of EOF exactly at the frame boundary, after the length prefix and before any body byte.
    // The host dying mid-frame is a lost channel: a frame that never finishes is not an end of scan.
    // A length prefix claiming ten bytes is followed by EOF partway through the body, or exactly
    // at the frame boundary before any body byte.
    [DataTestMethod]
    [DataRow(new byte[] { 10, 0, 0, 0, 1, 2, 3 }, DisplayName = "TruncatedFrame")]
    [DataRow(new byte[] { 10, 0, 0, 0 }, DisplayName = "HeaderOnlyThenEof")]
    public async Task ScanDrive_FrameCutShortByEof_IsChannelLost(byte[] rawBytes)
    {
        var lost = await ScanScriptedAsync(pipe => WriteRawAsync(pipe, rawBytes));

        Assert.AreEqual('C', lost.DriveLetter);
        StringAssert.Contains(lost.Message, "Truncated");
    }

    [TestMethod]
    public async Task ScanDrive_WithoutKeepFileNames_SendsAnEmptyList()
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(),
            new BrokerScanOptions { Profile = BrokerScanProfile.Full }, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using (var pipe = await broker.AcceptChannelAsync())
        {
            var request = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, request?.Kind);
            Assert.AreEqual(BrokerScanProfile.Full, request?.Profile);
            Assert.AreEqual(0, request?.KeepFileNames.Count);
        }

        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task Producer_ConnectsExactlyOnce()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var process = broker.Process;
        var connections = new System.Collections.Concurrent.ConcurrentQueue<CancellationToken>();
        var producer = new BrokerMftBlockProducer(connectToken =>
        {
            connections.Enqueue(connectToken);
            return Task.FromResult(process);
        }).CreateIndexSource().Producer;
        var target = TestBlockSections.Target();

        var produced = await producer(new MftBlockProduceRequest
        {
            DriveLetter = 'C',
            VolumeSerial = target.VolumeSerial,
            BlockPath = target.Path,
            DeleteOnClose = true
        }, CancellationToken.None).WaitAsync(HangGuard);
        produced.Block.Dispose();

        Assert.AreEqual(1, connections.Count);
    }

    // Runs one scan against a scripted drive pipe that writes the given frames and closes.
    static async Task<BrokerChannelLostException> ScanScriptedAsync(params Func<Stream, Task>[] frames)
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using (var pipe = await broker.AcceptChannelAsync())
        {
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
            foreach (var frame in frames)
            {
                await frame(pipe);
            }
        }

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        AssertSectionReleased(broker.Sections.Single());
        return lost;
    }

    static async Task WriteRawAsync(Stream pipe, byte[] bytes)
    {
        await pipe.WriteAsync(bytes).AsTask().WaitAsync(HangGuard);
        await pipe.FlushAsync().WaitAsync(HangGuard);
    }

    static Func<BrokerChannelConnector, BrokerChannelConnector> RecordHostEnd(
        TaskCompletionSource<DisposalRecordingStream> hostEnd)
    {
        return connect => async (pipeName, cancellationToken) =>
        {
            var recording = new DisposalRecordingStream(await connect(pipeName, cancellationToken));
            hostEnd.TrySetResult(recording);
            return recording;
        };
    }

    static JournalCheckpointLoss TrimmedLoss()
    {
        return new JournalCheckpointLoss('C', JournalCheckpointLossDetection.ScanCatchUp, JournalCheckpointLossCause.CheckpointTrimmed,
            new UsnJournalSettings { AllocationDelta = 4096, MaximumSize = 32768 })
        {
            CheckpointUsn = Armed.NextUsn,
            FirstUsn = 5000,
            NextUsn = 9000,
            BytesBehind = 4000,
            SizeThatWouldHaveRetained = 12288
        };
    }

    static void AssertSectionReleased((string SectionName, BlockFile Block, RecordingLifetime Lifetime) section)
    {
        Assert.IsTrue(section.Lifetime.IsDisposed, "The section's name must be released.");
        BlockFileAssertions.IsDisposed(section.Block);
    }
}
