using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Unified broker-host scan suite; exceeds 500 lines because issue 792 folds the three host-scan files into one class.
// Catch-up cases install process-global journal overrides, so this class runs nonparallel.
[TestClass]
[DoNotParallelize]
public class JournalBrokerHostScanTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly UsnJournalCursor ScanArmedCursor = new(7UL, 100L);
    static readonly UsnJournalCursor ArmedCursor = new(71, 12345);
    static readonly NtfsVolumeInformation ControlVolume = new(1024 * 1000, 1024);

    static readonly MftRecord[] RetentionSampleRecords =
    [
        new(100, 5, new MftRecordFields(3, FileAttributes.Directory), "repo"),
        new(101, 100, new MftRecordFields(1, FileAttributes.Archive), ".git"),
        new(102, 100, new MftRecordFields(1, FileAttributes.Archive), "file.txt")
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ScanChannel_EmitsCursorProgressReadyAndCatchUpInOrderThenCloses()
    {
        using var sectionWriter = new RecordingBlockSectionWriter();
        var capturedBlock = sectionWriter.Block;
        var cursorArmed = false;
        UsnJournalCursor? caughtUpFrom = null;
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ =>
            {
                cursorArmed = true;
                return Armed;
            },
            ScanDrive = (drive, _, reporting, _, _, _) =>
            {
                Assert.IsTrue(cursorArmed);
                Assert.AreEqual(new VolumeKey('C'), drive);
                reporting.Operation!.Processing("MFT parse");
                reporting.Progress?.Report(new BlockWriteProgress(2, 0, 2, null, BlockScanPhase.Parsing));
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]];
            },
            ReadJournal = (_, since, maximumBufferReads) =>
            {
                Assert.IsTrue(capturedBlock.Header.IsComplete);
                Assert.AreEqual(256, maximumBufferReads);
                caughtUpFrom ??= since;
                var tip = new UsnJournalCursor(7, 1500);
                return since == tip ? ([], since) : ([JournalEntries.Create(20, 1200, "file.txt")], tip);
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, sectionWriter);
        var sectionName = NamedBlockSection.BuildSectionName('C');

        var pipe = await harness.OpenScanChannelAsync('C', sectionName);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(Armed, frames[0].Cursor);
        Assert.IsTrue(frames.Skip(1).Take(frames.Count - 3).All(frame => frame.Kind == BrokerFrameKind.ScanProgress));
        Assert.IsGreaterThan(3, frames.Count, "At least the final progress sample precedes ScanReady.");
        var finalProgress = frames[^3].Progress!.Value;
        Assert.AreEqual(BrokerScanPhase.Transferring, finalProgress.Phase);
        Assert.ContainsSingle(frame => frame.Kind == BrokerFrameKind.ScanReady, frames);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(21u, sectionWriter.Block.Header.RowCount);
        Assert.AreEqual(2, InUseRowCount(sectionWriter));
        Assert.AreEqual(18u, sectionWriter.Block.Header.NamePoolUsed);
        Assert.AreEqual(sectionWriter.Block.Header.NamePoolUsed, finalProgress.BytesProcessed);
        Assert.IsTrue(sectionWriter.Block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, sectionWriter.Block.Header.ProducerKind);
        Assert.AreEqual(5u, sectionWriter.Block.Header.RootRow);
        Assert.AreEqual(Armed.JournalIdentifier, sectionWriter.Block.Header.UsnJournalIdentifier);
        Assert.AreEqual(Armed.NextUsn, sectionWriter.Block.Header.UsnNextUsn);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(sectionWriter.Block, 20).ToString());
        Assert.AreEqual(0L, frames[^2].SkippedRecordCount);
        Assert.ContainsSingle(frame => frame.Kind == BrokerFrameKind.ScanCompleted, frames);
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(default(UsnJournalCursor), frames[^1].Cursor);
        Assert.IsEmpty(frames[^1].Entries, "Catch-up entries stay on the host.");
        Assert.AreEqual(Armed, caughtUpFrom);
        Assert.AreEqual(sectionName, sectionWriter.LastSectionName);
        Assert.StartsWith("mftlib-block-C-", sectionWriter.LastSectionName!);
        Assert.IsTrue(frames.All(frame => frame.Drive == null && frame.RequestId == 0),
            "A drive pipe carries no drive and no request id.");
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.Error, frames);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalProvesLoss_EmitsScanReadyThenCatchUpLostAndCloses()
    {
        var cursorQueries = 0;
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"),
            () => Interlocked.Increment(ref cursorQueries));

        AssertScanReadyThen(frames, BrokerFrameKind.CatchUpLost);
        var loss = frames[^1].RequireCatchUpLoss('C');
        Assert.AreEqual('C', loss.Drive);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(Armed.NextUsn, loss.CheckpointUsn);
        Assert.AreEqual(5000L, loss.FirstUsn);
        Assert.AreEqual(9000L, loss.NextUsn);
        Assert.AreEqual(4096L, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(32768L, loss.JournalSettings.MaximumSize);
        Assert.AreEqual(4000L, loss.BytesBehind);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
        Assert.IsNull(frames[^1].Message, "The failure text stays on the host.");
        Assert.AreEqual(1, cursorQueries, "The host must not query a fresh cursor after a failed catch-up.");
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndCursorStillRetained_EmitsErrorAfterScanReady()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 500, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalCannotAnswer_EmitsErrorAfterScanReady()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalRecreated_CatchUpLostHasNoSize()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(8, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.CatchUpLost);
        var loss = frames[^1].RequireCatchUpLoss('C');
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpCancelled_WritesNoCatchUpLost()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new OperationCanceledException());

        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^1].Kind);
        Assert.DoesNotContain(frame => frame.Kind is BrokerFrameKind.CatchUpLost or BrokerFrameKind.Error
            or BrokerFrameKind.ScanCompleted, frames);
    }

    [TestMethod]
    public async Task ScanChannel_AllowanceReachesSource()
    {
        int? allowance = null;
        var host = BrokerHostFactory.Create(new()
        {
            ProcessorCount = 8,
            ScanDrive = (_, parseThreads, _, _, _, _) =>
            {
                allowance = parseThreads.Count;
                return [[BrokerHostFactory.Record(5, ".", 3)]];
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(8, allowance);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);
        var tip = new UsnJournalCursor(7, 1500);
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            ReadJournal = (_, _, _) => ([JournalEntries.Create(20, 1200, "file.txt")], tip),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        // The source broke its contract: a read from the tip returned entries again. Shipping them
        // would duplicate the first read's entries, so the scan fails loudly instead.
        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.Contains("without advancing", frames[^1].Message!);
    }

    [TestMethod]
    public async Task MftRecordBatchSource_StreamsBatchesToBlockSectionWriter()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => new UsnJournalCursor(7UL, 0L),
            ScanDrive = (_, _, _, _, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "batch1.txt")],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "batch2.txt")]
            ],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.Contains(f => f.Kind == BrokerFrameKind.ScanProgress, frames);
        Assert.ContainsSingle(f => f.Kind == BrokerFrameKind.ScanReady, frames);
        Assert.AreEqual(3u, blockWriter.Block.Header.RowCount);
        Assert.AreEqual("batch1.txt", NamePool.ReadRowName(blockWriter.Block, 1).ToString());
        Assert.AreEqual("batch2.txt", NamePool.ReadRowName(blockWriter.Block, 2).ToString());
        Assert.AreEqual(2, InUseRowCount(blockWriter));
    }

    [TestMethod]
    [DataRow(new[] { ".git" }, 2, DisplayName = "RetainedFileNameMatch_RetainsTheNamedFile")]
    [DataRow(new[] { ".GIT" }, 2, DisplayName = "RetainedFileNameMatch_IsCaseInsensitive")]
    [DataRow(new[] { "other.txt" }, 1, DisplayName = "NonMatchingFiles_AreDropped")]
    [DataRow(null, 3, DisplayName = "NullRetention_YieldsAllRecords")]
    [DataRow(new string[0], 1, DisplayName = "EmptyRetainedFileNames_YieldsDirectoriesOnly")]
    public async Task RetainedFileNames_DecideWhichFilesAreRetained(string[]? retainedFileNames, int expectedInUseRows)
    {
        using var writer = await ServeRetentionScanAsync(RetentionSampleRecords, retainedFileNames);

        Assert.AreEqual(expectedInUseRows, InUseRowCount(writer));
    }

    [TestMethod]
    public async Task ArmAndScan_InvalidFreedFlag_WritesMalformedErrorAndSessionContinues()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ScanArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [[ScanRecord(5, ".", 3)]],
            QueryVolumeInformation = _ => ControlVolume
        });
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe,
            writer =>
            {
                var buffer = new ArrayBufferWriter<byte>();
                BrokerProtocol.WriteArmAndScan(buffer, "section");
                var bytes = buffer.WrittenSpan.ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(23), 99);
                var destination = writer.GetSpan(bytes.Length);
                bytes.CopyTo(destination);
                writer.Advance(bytes.Length);
            });
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.Contains("malformed", frames[0].Message!);
        Assert.Contains("99", frames[0].Message!);
        Assert.IsNull(blockWriter.LastSectionName, "No scan runs for a request the host could not decode.");
        await JournalBrokerHostTests.AssertControlStillAnswersAsync(harness);
    }

    [TestMethod]
    public async Task DriveFailure_EmitsErrorFrameAndSessionContinues()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => throw new InvalidOperationException("journal wrapped"),
            ScanDrive = (_, _, _, _, _, _) => [[ScanRecord(5, ".", 3)]],
            QueryVolumeInformation = _ => ControlVolume
        });
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness, 'D');

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual(0u, frames[0].RequestId);
        Assert.AreEqual("journal wrapped", frames[0].Message);
        await JournalBrokerHostTests.AssertControlStillAnswersAsync(harness);
    }

    [TestMethod]
    public async Task CatchUpThrows_EmitsErrorAfterScanReady_OtherDriveUnaffected()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = drive => drive == 'C' ? new UsnJournalCursor(1UL, 100L) : new UsnJournalCursor(2UL, 200L),
            ScanDrive = (_, _, _, _, _, _) => [[ScanRecord(5, ".", 3)]],
            ReadJournal = (drive, since, _) => drive == 'C'
                ? throw new InvalidOperationException("journal wrapped")
                : (Array.Empty<UsnJournalEntry>(), since),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var failing = await harness.OpenScanChannelAsync('C');
        var healthy = await harness.OpenScanChannelAsync('D');
        var failingFrames = await HostChannelHarness.ReadToEndAsync(failing);
        var healthyFrames = await HostChannelHarness.ReadToEndAsync(healthy);

        Assert.AreEqual(BrokerFrameKind.Cursor, failingFrames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, failingFrames[^2].Kind);
        Assert.AreEqual(BrokerFrameKind.Error, failingFrames[^1].Kind);
        Assert.AreEqual("journal wrapped", failingFrames[^1].Message);
        Assert.DoesNotContain(f => f.Kind == BrokerFrameKind.ScanCompleted, failingFrames,
            "A failed catch-up ships no batch that would let the client treat the scan as caught up.");

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, healthyFrames[^1].Kind);
        Assert.AreEqual(new UsnJournalCursor(2UL, 200L), healthyFrames[0].Cursor);
        Assert.DoesNotContain(f => f.Kind == BrokerFrameKind.Error, healthyFrames);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void RealBlockSectionWriter_WritesBlock_ClientCanReadItBack()
    {
        using var blockDirectory = new TempDirectory("mftlib-broker-block");
        var sectionName = NamedBlockSection.BuildSectionName('C');
        var (block, lifetime) = NamedBlockSection.Create(
            TestBlocks.Options(Path.Combine(blockDirectory.Path, "block.bin"), ProducerKind.Mft, 128, 1024,
                volumeSerial: 123, rootRow: 5, deleteOnClose: true),
            sectionName);
        using (block)
        using (lifetime)
        {
            var cursor = new UsnJournalCursor(7, 100);
            var written = new RealBlockSectionWriter(TimeProvider.System).Write(sectionName, cursor,
                [[new MftRecord(5, 5, new MftRecordFields(3), "."),
                    new MftRecord(100, 5, new MftRecordFields(1, FileAttributes.Normal, 2048), "nöte.txt")]],
                new MftRowSelection(new MftScanOptions()), default, CancellationToken.None);

            Assert.AreEqual(101L, written.RowCount);
            Assert.AreEqual(18L, written.NamePoolUsedBytes);
            Assert.AreEqual(0L, written.SkippedRecordCount);
            Assert.IsTrue(block.Header.IsComplete);
            Assert.AreEqual(cursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
            Assert.AreEqual(cursor.NextUsn, block.Header.UsnNextUsn);
            Assert.AreEqual("nöte.txt", NamePool.ReadRowName(block, 100).ToString());
            Assert.AreEqual(2048L, block.Rows[100].Size);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task ArmAndScan_SectionHeaderRewrittenBeforeOpen_FailsTheScanWithTheRejection()
    {
        using var blockDirectory = new TempDirectory("mftlib-broker-block");
        var sectionName = NamedBlockSection.BuildSectionName('C');
        var (block, lifetime) = NamedBlockSection.Create(
            TestBlocks.Options(Path.Combine(blockDirectory.Path, "block.bin"), ProducerKind.Mft, 128, 1024,
                volumeSerial: 123, rootRow: 5, deleteOnClose: true),
            sectionName);
        using (block)
        using (lifetime)
        {
            block.Header.NamePoolOffset = BlockLayout.RowRegionOffset;
            var host = BrokerHostFactory.Create(new()
            {
                QueryCursor = _ => ScanArmedCursor,
                ScanDrive = (_, _, _, _, _, _) => [[ScanRecord(100, "a.txt")]],
                QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
            });
            await using var harness = new HostChannelHarness(host, new RealBlockSectionWriter(TimeProvider.System));

            var frames = await ScanFramesAsync(harness, 'C', sectionName);

            Assert.AreEqual(BrokerFrameKind.Error, frames[^1].Kind);
            Assert.Contains(nameof(BlockValidationResult.InconsistentRegions), frames[^1].Message!);
            Assert.AreEqual(0u, block.Header.RowCount, "a refused section receives no rows");
        }
    }

    [TestMethod]
    public async Task ScanChannel_ForwardsRetainedFileNamesToSectionWriter()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });

        var frames = await ScanAsync(host, blockWriter, [".git"]);

        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.Error, frames);
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.StartsWith("mftlib-block-C-", blockWriter.LastSectionName!);
        Assert.IsNotNull(blockWriter.LastRetainedFileNames);
        Assert.AreSequenceEqual([".git"], [.. blockWriter.LastRetainedFileNames]);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, blockWriter.Block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.None, blockWriter.Block.Rows[20].Flags);
    }

    [TestMethod]
    public async Task ScanChannel_RecordsTheSourceOmitted_AreAddedToTheSkippedRecordCount()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, recordsOmitted, _) =>
            {
                recordsOmitted(3);
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt"), BrokerHostFactory.Record(uint.MaxValue + 1UL, "wide")]];
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });

        var frames = await ScanAsync(host, blockWriter);

        Assert.AreEqual(4L, frames.Single(frame => frame.Kind == BrokerFrameKind.ScanReady).SkippedRecordCount,
            "three left out by the source and one the writer could not place");
    }

    [TestMethod]
    public async Task ScanChannel_WithoutSectionWriterReportsNamedError()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });

        var frames = await ScanAsync(host, null);

        Assert.Contains("blockSectionWriter",
            frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message!);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanReady, frames);
    }

    [TestMethod]
    public async Task ScanChannel_BlockSourceFailureLeavesIncompleteBlockAndEmitsNoScanReady()
    {
        using var blockWriter = new RecordingBlockSectionWriter();

        static IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            yield return [BrokerHostFactory.Record(5, ".", 3)];
            throw new IOException("record batch failed");
        }

        var frames = await ScanAsync(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => Batches(),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        }), blockWriter);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(0UL, blockWriter.Block.Header.UsnJournalIdentifier);
        Assert.AreEqual("record batch failed", frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanReady, frames);
    }

    [TestMethod]
    public async Task ScanChannel_BlockProgressReportsParsingThenTransferring()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var parsingReported = new TestGate();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, reporting, _, _, _) =>
            {
                reporting.Progress!.Report(new BlockWriteProgress(500, 0, 1000, null, BlockScanPhase.Parsing));
                parsingReported.MarkEntered();
                parsingReported.WaitForRelease();
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]];
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C');

        await HostChannelHarness.ReadFrameAsync(pipe); // Cursor
        var firstProgress = (await HostChannelHarness.ReadFrameAsync(pipe))!.Value;
        Assert.AreEqual(BrokerScanPhase.Parsing, firstProgress.Progress?.Phase);
        Assert.AreEqual(500L, firstProgress.Progress?.RecordsProcessed);
        Assert.AreEqual(1000L, firstProgress.Progress?.TotalRecords);
        parsingReported.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var transfers = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress).ToArray();
        Assert.IsNotEmpty(transfers);
        Assert.IsTrue(transfers.All(frame => frame.Progress?.Phase == BrokerScanPhase.Transferring));
        Assert.AreEqual(18L, transfers[^1].Progress?.BytesProcessed);
        Assert.AreEqual(transfers[^1].Progress?.BytesProcessed, transfers[^1].Progress?.TotalBytes);
        Assert.AreEqual(1000L, transfers[^1].Progress?.TotalRecords);
    }

    [TestMethod]
    public async Task ScanChannel_CancelledBlockScanLeavesIncompleteBlockWithoutErrorOrScanReady()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var scanParked = new TestGate();

        // The second batch is yielded only once the session has ended, so the writer meets a
        // cancelled token before it can write it.
        IEnumerable<IReadOnlyList<MftRecord>> Batches(CancellationToken cancellationToken)
        {
            yield return [BrokerHostFactory.Record(5, ".", 3)];
            scanParked.MarkEntered();
            if (!cancellationToken.WaitHandle.WaitOne(HangGuard * 3))
            {
                throw new TimeoutException("The scan was never cancelled.");
            }

            yield return [BrokerHostFactory.Record(20, "file.txt")];
        }

        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, cancellationToken) => Batches(cancellationToken),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C');
        await scanParked.Entered.WaitAsync(HangGuard * 3, TestContext.CancellationToken);

        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HangGuard * 3, TestContext.CancellationToken);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.DoesNotContain(frame => frame.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.Error, frames);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("other-block-C-0123456789abcdef0123456789abcdef")]
    [DataRow("MFTLIB-BLOCK-C-0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-block-D-0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-block-c-0123456789abcdef0123456789abcdef")]
    [DataRow(@"mftlib-block-C-0123456789abcdef\123456789abcdef")]
    [DataRow(@"Global\mftlib-block-C-0123456789abcdef0123456789abcdef")]
    [DataRow(@"Local\mftlib-block-C-0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-block-C_0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-block-C0123456789abcdef0123456789abcdef0")]
    [DataRow("mftlib-block-C-0123456789abcdef0123456789abcde")]
    [DataRow("mftlib-block-C-0123456789abcdef0123456789abcdef0")]
    [DataRow("mftlib-block-C-0123456789abcdef0123456789abcdeg")]
    public async Task ScanChannel_InvalidSectionName_WritesErrorWithoutTouchingVolumeOrSectionWriter(
        string invalidSectionName)
    {
        var cursorQueries = 0;
        var scanCalls = 0;
        using var writer = new RecordingBlockSectionWriter();
        var volumeInfo = new NtfsVolumeInformation(1024 * 1000, 1024);
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ =>
            {
                Interlocked.Increment(ref cursorQueries);
                return ArmedCursor;
            },
            ScanDrive = (_, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref scanCalls);
                return [[BrokerHostFactory.Record(5, ".", 3)]];
            },
            QueryVolumeInformation = _ => volumeInfo,
            ProcessorCount = null
        });
        await using var harness = new HostChannelHarness(host, writer);

        var pipe = await harness.OpenScanChannelAsync('C', invalidSectionName);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);
        await harness.SendControlAsync(bufferWriter => BrokerProtocol.WriteQueryVolume(bufferWriter, 51, 'C'));
        var controlReply = await harness.ReadControlAsync();

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual(0u, frames[0].RequestId);
        Assert.Contains("invalid section name", frames[0].Message!);
        Assert.IsNull(writer.LastSectionName);
        Assert.AreEqual(0, cursorQueries);
        Assert.AreEqual(0, scanCalls);
        Assert.AreEqual(BrokerFrameKind.VolumeInfo, controlReply.Kind);
        Assert.AreEqual(51u, controlReply.RequestId);
    }

    [TestMethod]
    [DataRow("mftlib-block-C-0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-block-C-ABCDEF0123456789ABCDEF0123456789")]
    [DataRow("mftlib-block-C-0123456789abcdefABCDEF0123456789")]
    public async Task ScanChannel_ValidHexSectionNames_AreAccepted(string validSectionName)
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });
        await using var harness = new HostChannelHarness(host, blockWriter);

        var pipe = await harness.OpenScanChannelAsync('C', validSectionName);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(validSectionName, blockWriter.LastSectionName);
    }

    [TestMethod]
    public async Task ScanChannel_ForwardsSharedScanArgumentsAndProducesExpectedRows()
    {
        MftRecord[][] batches =
        [
            [BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(10, "folder", 3),
                new MftRecord(20, 10, new MftRecordFields(1), "file.txt"), BrokerHostFactory.Record(21, "")],
            [BrokerHostFactory.Record(30, "other.txt"), new MftRecord(1UL << 33, 5, new MftRecordFields(1), "huge")]
        ];
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        using var hostWriter = new RecordingBlockSectionWriter(timeProvider: timeProvider);
        var hostFrames = await ScanAsync(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => batches,
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        }), hostWriter);
        var skippedByHost = hostFrames.Single(frame => frame.Kind == BrokerFrameKind.ScanReady).SkippedRecordCount;

        using var blockDirectory = new TempDirectory("mftlib-broker-block");
        using var directBlock = TestBlocks.Create(Path.Combine(blockDirectory.Path, "block.bin"),
            ProducerKind.Mft, 256, 4096, volumeSerial: 123, rootRow: 5, deleteOnClose: true);
        var result = MftBlockScan.WriteToBlock(directBlock, new BlockStamp(ArmedCursor, timeProvider), batches,
            new MftRowSelection(new MftScanOptions()), default, CancellationToken.None);

        Assert.AreEqual(skippedByHost, result.SkippedRecordCount);
        Assert.AreEqual(2L, result.SkippedRecordCount);
        Assert.AreSequenceEqual(BlockBytes(hostWriter.Block), BlockBytes(directBlock));
        await AssertExpectedRowsAsync(hostWriter.Block);
        await AssertExpectedRowsAsync(directBlock);
    }

    static async Task<List<BrokerFrame>> ScanWithFailedCatchUpAsync(Exception failure, Action? onCursorQuery = null)
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ =>
            {
                onCursorQuery?.Invoke();
                return Armed;
            },
            ReadJournal = (_, _, _) => throw failure,
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        return await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));
    }

    static void AssertScanReadyThen(List<BrokerFrame> frames, BrokerFrameKind terminal)
    {
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(terminal, frames[^1].Kind);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanCompleted, frames);
    }

    static async Task<RecordingBlockSectionWriter> ServeRetentionScanAsync(
        MftRecord[] records, IReadOnlyCollection<string>? retainedFileNames)
    {
        var writer = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ScanArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [records],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, writer);

        var frames = await ScanFramesAsync(harness, retainedFileNames: retainedFileNames);

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        return writer;
    }

    static MftRecord ScanRecord(ulong recordNumber, string name, ushort flags = 1) =>
        new(recordNumber, 5, new MftRecordFields(flags, FileAttributes.Archive, 100), name);

    static async Task<List<BrokerFrame>> ScanFramesAsync(HostChannelHarness harness, char drive = 'C',
        string? sectionName = null, IReadOnlyCollection<string>? retainedFileNames = null)
    {
        var pipe = await harness.OpenScanChannelAsync(drive, sectionName, retainedFileNames);
        return await HostChannelHarness.ReadToEndAsync(pipe);
    }

    static async Task<List<BrokerFrame>> ScanAsync(JournalBrokerHost host, IBlockSectionWriter? blockWriter,
        IReadOnlyCollection<string>? retainedFileNames = null)
    {
        await using var harness = new HostChannelHarness(host, blockWriter);
        return await ScanFramesAsync(harness, retainedFileNames: retainedFileNames);
    }

    static int InUseRowCount(RecordingBlockSectionWriter writer) =>
        writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0);

    static async Task AssertExpectedRowsAsync(BlockFile block)
    {
        Assert.AreEqual(4u, block.Header.LiveRowCount);
        Assert.AreEqual(31u, block.Header.RowCount);
        Assert.AreEqual(RowFlags.None, block.Rows[21].Flags);
        var rootPath = Path.Combine(Path.GetDirectoryName(block.Path)!, "scan-oracle");
        DriveBlock[] snapshot = [new DriveBlock(new VolumeKey('C'), 0, block, rootPath)];
        try
        {
            (string RelativePath, uint Row, string Name, uint Parent, RowFlags Flags)[] expected =
            [
                ("", 5, ".", 5, RowFlags.InUse | RowFlags.Directory),
                ("folder", 10, "folder", 5, RowFlags.InUse | RowFlags.Directory),
                ("folder/file.txt", 20, "file.txt", 10, RowFlags.InUse),
                ("other.txt", 30, "other.txt", 5, RowFlags.InUse)
            ];
            foreach (var row in expected)
            {
                var entry = LookupEngine.Find(snapshot, Path.Combine(rootPath, row.RelativePath));
                Assert.IsNotNull(entry, $"Expected lookup for {row.RelativePath}");
                Assert.AreEqual(row.Row, entry.Value.RowIndex);
                Assert.AreEqual(row.Name, entry.Value.Name);
                Assert.AreEqual(row.Parent, entry.Value.Row.ParentRow);
                Assert.AreEqual(row.Flags, entry.Value.Row.Flags);
            }

            Assert.IsNull(LookupEngine.Find(snapshot, Path.Combine(rootPath, "huge")));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    static byte[] BlockBytes(BlockFile block)
    {
        var header = block.Header;
        return
        [
            .. MemoryMarshal.AsBytes(new ReadOnlySpan<BlockHeader>(in header)),
            .. MemoryMarshal.AsBytes(block.Rows),
            .. MemoryMarshal.AsBytes(block.SequenceNumbers),
            .. MemoryMarshal.AsBytes(block.NamePoolCharacters)
        ];
    }
}
