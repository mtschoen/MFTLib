using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    // Puts the armed cursor (7, 1000) below the journal's first USN: a proven trim.
    static readonly SyntheticJournalWindow TrimmedWindow = new(7, 5000, 9000, 4096, 32768);

    // Still holds the armed cursor: a catch-up failure the journal does not prove.
    static readonly SyntheticJournalWindow RetainedWindow = new(7, 500, 9000, 4096, 32768);

    [TestMethod]
    public async Task ScanVolume_ReturnsArmedCursorAndBlockAfterCatchUp()
    {
        var advanced = new UsnJournalCursor(7, 1500);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadJournal = CatchUpSources.ToTip(advanced, JournalEntries.Create(20, 1200, "file.txt"))
        }));
        var target = TestBlockSections.Target() with { CacheTag = new CacheTag("TEST", 3) };

        var result = await broker.Process.ScanVolumeAsync('c', target, new MftScanOptions(), null, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block.Block;

        var (_, sectionBlock, lifetime) = broker.Sections.Single();
        Assert.AreEqual(new VolumeKey('C'), result.Volume);
        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.AreEqual(Armed.NextUsn, block.Header.UsnNextUsn);
        Assert.AreNotEqual(advanced.NextUsn, block.Header.UsnNextUsn);
        Assert.IsNull(result.CatchUpLoss);
        Assert.AreSame(sectionBlock, block);
        Assert.IsGreaterThan(0u, block.Header.RowCount);
        Assert.AreEqual(Armed.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(target.CacheTag, block.Header.CacheTag);
        Assert.AreEqual(target.Path, block.Path);
        Assert.IsTrue(lifetime.IsDisposed, "The section name is unpublished once the block is written.");
    }

    [TestMethod]
    public async Task ScanVolume_TheTargetMinimumSizesTheSection()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var baselineResult = await broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var baseline = baselineResult.Block.Block;
        var target = TestBlockSections.Target() with
        {
            MinimumCapacities = new BlockCapacities(0, 0, 2 * baseline.NamePoolCapacity)
        };

        var grownResult = await broker.Process.ScanVolumeAsync('C', target, new MftScanOptions(),
            null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var grown = grownResult.Block.Block;

        Assert.AreEqual(2 * baseline.NamePoolCapacity, grown.NamePoolCapacity);
        Assert.AreEqual(baseline.SlotCapacity, grown.SlotCapacity);
        Assert.AreEqual(baseline.NameEntryCapacity, grown.NameEntryCapacity);
    }

    [TestMethod]
    public async Task ScanVolume_ReportsProgress()
    {
        var reports = new List<BlockWriteProgress>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, reporting, _, _, _) =>
            {
                reporting.Progress?.Report(new BlockWriteProgress(2, 0, 2, null, BlockScanPhase.Parsing));
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]];
            }
        }));
        var progressSink = new SynchronousProgress<BlockWriteProgress>(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        });

        var result = await broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(), progressSink, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        result.Block.Block.Dispose();

        Assert.IsNotEmpty(reports);
        Assert.AreEqual(BlockScanPhase.Transferring, reports[^1].Phase);
    }

    [TestMethod]
    public async Task ScanVolume_CatchUpLost_ReturnsBlockWithLossAndArmedCursor()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => TrimmedWindow);
        var hostEnd = new TaskCompletionSource<DisposalRecordingStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(
            BrokerHostFactory.Create(new() { ReadJournal = (_, _, _) => throw new IOException("catch-up read failed") }),
            wrapConnector: RecordHostEnd(hostEnd));

        var (_, armedCursor, loss, producedBlock) = await broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = producedBlock.Block;

        Assert.AreEqual(Armed, armedCursor);
        Assert.IsNotNull(loss);
        Assert.AreEqual('C', loss.Volume);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(Armed.NextUsn, loss.CheckpointUsn);
        Assert.AreEqual(5000L, loss.FirstUsn);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
        Assert.IsGreaterThan(0u, block.Header.RowCount, "The section stays alive for the caller.");
        await (await hostEnd.Task.WaitAsync(HangGuard, TestContext.CancellationToken)).Disposed.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ScanVolume_ErrorAfterScanReady_DisposesSectionAndReturnsNoBlock()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => RetainedWindow);
        await using var broker = new InProcessBroker(
            BrokerHostFactory.Create(new() { ReadJournal = (_, _, _) => throw new IOException("catch-up read failed") }));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
                null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual("catch-up read failed", exception.Message);
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task ScanVolume_CatchUpLostBeforeScanReady_IsProtocolError()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, BrokerFrame.ArmedCursor(Armed))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteCatchUpLost(writer, BrokerFrame.CatchUpLost(TrimmedLoss()))));

        Assert.AreEqual('C', lost.Volume);
        Assert.Contains("CatchUpLost", lost.Message);
    }

    [TestMethod]
    public async Task ScanVolume_ScanCompletedAfterCatchUpLost_IsProtocolError()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, BrokerFrame.ArmedCursor(Armed))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, BrokerFrame.ScanReady(0))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteCatchUpLost(writer, BrokerFrame.CatchUpLost(TrimmedLoss()))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanCompleted(writer, BrokerFrame.ScanCompleted())));

        Assert.AreEqual('C', lost.Volume);
        Assert.Contains("ScanCompleted", lost.Message);
    }

    [TestMethod]
    public async Task ScanVolume_JournalBatchAsTerminalFrame_IsProtocolError()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, BrokerFrame.ArmedCursor(Armed))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, BrokerFrame.ScanReady(0))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe,
                writer => BrokerProtocol.WriteJournalBatch(writer, BrokerFrame.JournalBatch(Armed, []))));

        Assert.AreEqual('C', lost.Volume);
        Assert.Contains("JournalBatch", lost.Message);
        Assert.Contains("ScanCompleted or CatchUpLost", lost.Message);
    }

    // After its terminal frame the host only closes the channel. A frame the host starts and never
    // finishes is a lost channel, not the EOF that completes the scan.
    [TestMethod]
    public async Task ScanVolume_TruncatedFrameAfterTerminal_IsChannelLost()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, BrokerFrame.ArmedCursor(Armed))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, BrokerFrame.ScanReady(0))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanCompleted(writer, BrokerFrame.ScanCompleted())),
            pipe => WriteRawAsync(pipe, [10, 0, 0, 0, (byte)BrokerFrameKind.Heartbeat]));

        Assert.AreEqual('C', lost.Volume);
        Assert.Contains("Truncated", lost.Message);
    }

    [TestMethod]
    public async Task ScanVolume_InvalidFrameLengthAfterTerminal_IsChannelLost()
    {
        var lost = await ScanScriptedAsync(
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteCursor(writer, BrokerFrame.ArmedCursor(Armed))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanReady(writer, BrokerFrame.ScanReady(0))),
            pipe => HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteScanCompleted(writer, BrokerFrame.ScanCompleted())),
            pipe => WriteRawAsync(pipe, BitConverter.GetBytes(0)));

        Assert.AreEqual('C', lost.Volume);
        Assert.Contains("too short", lost.Message);
    }

    [TestMethod]
    public async Task Producer_CatchUpLost_CopiesLossToProduceResult()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => TrimmedWindow);
        await using var broker = new InProcessBroker(
            BrokerHostFactory.Create(new() { ReadJournal = (_, _, _) => throw new IOException("catch-up read failed") }));
        var process = broker.Process;
        var producer = new BrokerMftBlockProducer(_ => Task.FromResult(process)).CreateIndexSource().Producer;
        var target = TestBlockSections.Target();

        var produced = await producer(new MftBlockProduceRequest
        {
            Volume = 'C',
            VolumeSerial = target.VolumeSerial,
            BlockPath = target.Path,
            DeleteOnClose = true
        }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = produced.Block;

        Assert.IsNotNull(produced.CatchUpLoss);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, produced.CatchUpLoss.DetectedDuring);
        Assert.AreEqual(5000L, produced.CatchUpLoss.FirstUsn);
        Assert.AreEqual(Armed.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(Armed.NextUsn, block.Header.UsnNextUsn);
        Assert.IsGreaterThan(0u, block.Header.RowCount);
    }

    [TestMethod]
    public async Task ScanVolume_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, cancellationToken) => WaitForCancellation(scanning, sourceCancelled, cancellationToken)
        }));
        using var cancellation = new CancellationTokenSource();

        var scan = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, cancellation.Token);
        await scanning.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await cancellation.CancelAsync();

        await AssertCancelledAsync(scan);
        await sourceCancelled.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task ScanVolume_CancelOne_OtherVolumeCompletes()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanVolume = (volume, _, _, _, _, cancellationToken) =>
                volume == 'C'
                    ? WaitForCancellation(scanning, sourceCancelled, cancellationToken)
                    : [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]]
        }));
        using var cancellation = new CancellationTokenSource();

        var cancelled = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, cancellation.Token);
        await scanning.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await cancellation.CancelAsync();
        await AssertCancelledAsync(cancelled);
        await sourceCancelled.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        var other = await broker.Process.ScanVolumeAsync('D', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = other.Block.Block;

        Assert.AreEqual(new VolumeKey('D'), other.Volume);
        Assert.IsGreaterThan(0u, block.Header.RowCount);
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task ScanVolume_TwoVolumesConcurrently_BothComplete()
    {
        var gates = new Dictionary<VolumeKey, TestGate> { ['C'] = new(), ['D'] = new() };
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanVolume = (volume, _, _, _, _, _) =>
            {
                gates[volume].MarkEntered();
                gates[volume].WaitForRelease();
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, volume.Letter + ".txt")]];
            }
        }));

        var scanC = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        var scanD = broker.Process.ScanVolumeAsync('D', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await Task.WhenAll(gates['C'].Entered, gates['D'].Entered).WaitAsync(HangGuard, TestContext.CancellationToken);
        gates['C'].Release();
        gates['D'].Release();
        var results = await Task.WhenAll(scanC, scanD).WaitAsync(HangGuard, TestContext.CancellationToken);

        foreach (var result in results)
        {
            using var block = result.Block.Block;
            Assert.IsGreaterThan(0u, block.Header.RowCount);
        }

        Assert.AreSequenceEqual(['C', 'D'], [.. results.Select(result => result.Volume)]);
    }

    [TestMethod]
    public async Task Producer_ReportsProgressToRequest()
    {
        var indexReports = new List<IndexScanProgress>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var process = broker.Process;
        var producer = new BrokerMftBlockProducer(_ => Task.FromResult(process), new MftScanOptions())
            .CreateIndexSource().Producer;
        var target = TestBlockSections.Target();

        var produced = await producer(new MftBlockProduceRequest
        {
            Volume = 'C',
            VolumeSerial = target.VolumeSerial,
            BlockPath = target.Path,
            DeleteOnClose = true,
            Progress = new SynchronousProgress<IndexScanProgress>(indexReports.Add)
        }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        produced.Block.Dispose();

        Assert.IsNull(produced.CatchUpLoss);
        Assert.IsNotEmpty(indexReports);
        Assert.IsTrue(indexReports.All(report => report.Root == new VolumeKey('C')));
        Assert.AreEqual(IndexScanPhase.Transferring, indexReports[^1].Phase);
        Assert.IsTrue(indexReports[^1].ExpectedRowCount > 0);
    }

    [TestMethod]
    public async Task ScanVolume_ProcessEndsBeforeChannelOpens_FailsNamingVolume()
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);

        await broker.CloseControlAsync();

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Volume);
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
    [TestMethod]
    [DataRow(new byte[] { 10, 0, 0, 0, 1, 2, 3 }, DisplayName = "TruncatedFrame")]
    [DataRow(new byte[] { 10, 0, 0, 0 }, DisplayName = "HeaderOnlyThenEof")]
    public async Task ScanVolume_FrameCutShortByEof_IsChannelLost(byte[] rawBytes)
    {
        var lost = await ScanScriptedAsync(pipe => WriteRawAsync(pipe, rawBytes));

        Assert.AreEqual('C', lost.Volume);
        Assert.Contains("Truncated", lost.Message);
    }

    [TestMethod]
    public async Task ScanVolume_SendsSectionAndIncludeFreed()
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(),
            new MftScanOptions(), null, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using (var pipe = await broker.AcceptChannelAsync())
        {
            var request = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, request?.Kind);
            Assert.IsNotNull(request);
            Assert.IsNotNull(request.Value.SectionName);
            Assert.IsFalse(request.Value.IncludeFreed);
        }

        await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        AssertSectionReleased(broker.Sections.Single());
    }

    [TestMethod]
    public async Task Producer_ConnectsExactlyOnce()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
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
            Volume = 'C',
            VolumeSerial = target.VolumeSerial,
            BlockPath = target.Path,
            DeleteOnClose = true
        }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        produced.Block.Dispose();

        Assert.HasCount(1, connections);
    }

    // Runs one scan against a scripted volume pipe that writes the given frames and closes.
    static async Task<BrokerChannelLostException> ScanScriptedAsync(params Func<Stream, Task>[] frames)
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(Volume);
        await using (var pipe = await broker.AcceptChannelAsync())
        {
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
            foreach (var frame in frames)
            {
                await frame(pipe);
            }
        }

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
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

    static BrokerCatchUpLoss TrimmedLoss()
    {
        return new BrokerCatchUpLoss(JournalCheckpointLossCause.CheckpointTrimmed, Armed.NextUsn, 5000, 9000, 4096, 32768, 4000, 12288);
    }

    static void AssertSectionReleased((string SectionName, BlockFile Block, RecordingLifetime Lifetime) section)
    {
        Assert.IsTrue(section.Lifetime.IsDisposed, "The section's name must be released.");
        BlockFileAssertions.IsDisposed(section.Block);
    }
}
