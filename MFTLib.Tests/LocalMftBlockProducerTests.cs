using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalMftBlockProducerTests : IndexTreeTestBase
{
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);
    static readonly CacheTag RequestedTag = new("LOCL", 3);

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(CacheDirectoryPath);

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name);
    }

    static IEnumerable<IReadOnlyList<MftRecord>> Batches() =>
        [[Record(5, ".", 3)], [Record(20, "file.txt"), Record(21, "other.md")]];

    static ScriptedVolumeAccess Scripted(
        Func<VolumeKey, NtfsVolumeInformation>? query = null, MftRecordBatchSource? scan = null,
        Func<VolumeKey, UsnJournalCursor>? queryCursor = null) => new()
        {
            VolumeInformation = query ?? (_ => Volume),
            RecordBatches = scan ?? ((_, _, _, _, _, _) => Batches()),
            CursorQuery = queryCursor ?? (_ => new UsnJournalCursor(99, 2000))
        };

    MftBlockProduceRequest Request(bool deleteOnClose = true, IProgress<IndexScanProgress>? progress = null,
        char drive = 'T') => new()
        {
            Drive = drive,
            VolumeSerial = 123,
            BlockPath = Path.Combine(CacheDirectoryPath,
            deleteOnClose ? $"{drive}-temporary.mlix" : $"{drive}-canonical.mlix"),
            DeleteOnClose = deleteOnClose,
            Progress = progress,
            CacheTag = RequestedTag
        };

    static MftBlockProducer ProducerOf(LocalMftBlockProducer producer) => producer.CreateIndexSource().Producer;

    FileIndexOptions Options(MftIndexSource source, IProgress<IndexScanProgress>? progress = null) =>
        Fixture.Options(producer: ProducerKind.Mft, cacheMode: CacheMode.Disabled) with
        {
            MftSource = source,
            ScanProgress = progress
        };

    [TestMethod]
    public void Factory_BuildsASourceWithNoWatchOrDump()
    {
        var source = MftIndexSources.FromLocalVolumes(new MftScanOptions());

        Assert.IsNull(source.WatchSource);
        Assert.IsNull(source.DumpIdentity);
    }

    [TestMethod]
    public void Factory_AcceptsNoScanOptions()
    {
        Assert.IsNotNull(MftIndexSources.FromLocalVolumes());
    }

    [TestMethod]
    public void Constructing_CallsNoSeam()
    {
        var calls = 0;
        var access = Scripted(_ =>
        {
            calls++;
            return Volume;
        }, (_, _, _, _, _, _) =>
        {
            calls++;
            return Batches();
        });

        _ = new LocalMftBlockProducer(TimeProvider.System, null, access).CreateIndexSource();

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Produce_DeleteOnCloseBlockCarriesGeometryTagAndZeroCursor()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero));
        IEnumerable<IReadOnlyList<MftRecord>> TimedBatches()
        {
            foreach (var batch in Batches())
            {
                yield return batch;
                timeProvider.Advance(TimeSpan.FromMinutes(1));
            }
        }

        var request = Request();
        var queried = new List<VolumeKey>();
        var producer = new LocalMftBlockProducer(timeProvider, null, Scripted(drive =>
        {
            queried.Add(drive);
            return Volume;
        }, scan: (_, _, _, _, _, _) => TimedBatches()));

        var result = await ProducerOf(producer)(request, TestContext.CancellationToken);
        var block = result.Block;

        Assert.AreSequenceEqual([new VolumeKey('T')], queried);
        Assert.AreEqual(request.BlockPath, block.Path);
        Assert.AreEqual(RequestedTag, block.Header.CacheTag);
        Assert.AreEqual(123u, block.Header.VolumeSerial);
        Assert.AreEqual(ProducerKind.Mft, block.Header.ProducerKind);
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(0UL, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(0L, block.Header.UsnNextUsn);
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.IsNull(result.CatchUpLoss);
        Assert.AreEqual(22u, block.Header.RowCount);
        Assert.AreEqual(new DateTime(2031, 2, 3, 4, 7, 6, DateTimeKind.Utc), block.Header.ScanTimestampUtc);
        var path = block.Path;
        block.Dispose();
        Assert.IsFalse(File.Exists(path), "a delete-on-close block is removed when the index releases it");

        await using var enumeration = await FileIndex.OpenAsync(
            FileIndexTestAccess.WithTimeProvider(
                Fixture.Options(volumeSerial: 123, cacheMode: CacheMode.Disabled),
                timeProvider), TestContext.CancellationToken);
        Assert.AreEqual(timeProvider.GetUtcNow().UtcDateTime, enumeration.Status.Block!.ScanTimestamp);
    }

    [TestMethod]
    public async Task Produce_SaturatesSkippedRecordCount()
    {
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, _, _, recordsOmitted, _) =>
        {
            recordsOmitted((long)int.MaxValue + 1);
            return [new[] { Record(5, ".", 3) }];
        }));

        var result = await ProducerOf(producer)(Request(), TestContext.CancellationToken);
        using var block = result.Block;

        Assert.AreEqual(int.MaxValue, result.SkippedRecordCount);
        Assert.IsTrue(block.Header.IsComplete);
    }


    [TestMethod]
    [DataRow(false, DisplayName = "normal-reopen-unmoved-journal")]
    [DataRow(true, DisplayName = "cache-only-reopen-unmoved-journal")]
    public async Task Produce_CachePathBlockSurvivesDisposal(bool cacheOnly)
    {
        using var journal = JournalIsolation.OverrideJournalWindow(
            _ => new SyntheticJournalWindow(99, 1000, 2000, 4096, 32768));
        var scanCalls = 0;
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref scanCalls);
            return Batches();
        }));
        var source = producer.CreateIndexSource();

        FileIndexOptions CachingOptions(bool isCacheOnly) =>
            Fixture.Options(producer: ProducerKind.Mft,
                cacheMode: isCacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent) with
            {
                MftSource = source
            };

        await using (var initialIndex = await FileIndex.OpenAsync(CachingOptions(isCacheOnly: false),
            TestContext.CancellationToken))
        {
            Assert.AreEqual(1, scanCalls);
            var initialDrive = initialIndex.Status;
            Assert.AreEqual(DriveState.Ready, initialDrive.State);
            Assert.AreEqual(BlockOrigin.ProducedByScan, initialDrive.Block!.Origin);
            Assert.IsNull(initialDrive.Watch.CheckpointLoss);
        }

        var canonicalPath = Path.Combine(CacheDirectoryPath,
            CacheDirectory.BlockFileName(new VolumeKey('T'), 0x0BADF00D));
        Assert.IsTrue(File.Exists(canonicalPath));

        await using (var reopenedIndex = await FileIndex.OpenAsync(CachingOptions(isCacheOnly: cacheOnly),
            TestContext.CancellationToken))
        {
            Assert.AreEqual(1, scanCalls, "A cached block must not scan again on reopening.");
            var reopenedDrive = reopenedIndex.Status;
            Assert.AreEqual(DriveState.Ready, reopenedDrive.State);
            Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopenedDrive.Block!.Origin);
            Assert.IsNull(reopenedDrive.Watch.CheckpointLoss);
        }
    }

    public enum ScanTermination
    {
        Completes,
        Fails,
        Cancels
    }

    [TestMethod]
    [DataRow(ScanTermination.Completes, DisplayName = "completion-releases-allowance")]
    [DataRow(ScanTermination.Fails, DisplayName = "failure-releases-allowance")]
    [DataRow(ScanTermination.Cancels, DisplayName = "cancellation-releases-allowance")]
    public async Task Produce_ConcurrentScans_ShareAllowanceAndReleaseOnTermination(ScanTermination termination)
    {
        var allocator = new ParseThreadAllocator(4);
        var gateT = new TestGate();
        var gateU = new TestGate();
        ParseThreadAllowance? allowanceT = null;
        ParseThreadAllowance? allowanceU = null;

        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (drive, allowance, _, _, _, token) =>
        {
            if (drive.Letter == 'T')
            {
                allowanceT = allowance;
                gateT.MarkEntered();
                gateT.WaitForRelease();
                if (termination == ScanTermination.Fails)
                {
                    throw new IOException("Scan for drive T failed.");
                }

                token.ThrowIfCancellationRequested();
            }
            else
            {
                allowanceU = allowance;
                gateU.MarkEntered();
                gateU.WaitForRelease();
                token.ThrowIfCancellationRequested();
            }

            return Batches();
        }), allocator);

        using var cancellationT = new CancellationTokenSource();
        var taskT = ProducerOf(producer)(Request(drive: 'T'), cancellationT.Token);
        await gateT.Entered.WaitAsync(TestContext.CancellationToken);
        Assert.AreEqual(4, allowanceT!.Count, "A single running scan takes the full thread budget.");

        var taskU = ProducerOf(producer)(Request(drive: 'U'), TestContext.CancellationToken);
        await gateU.Entered.WaitAsync(TestContext.CancellationToken);
        Assert.AreEqual(2, allowanceT.Count, "Admission of a second scan halves the running scan's allowance.");
        Assert.AreEqual(2, allowanceU!.Count, "The second scan receives half the allowance.");

        switch (termination)
        {
            case ScanTermination.Completes:
                gateT.Release();
                var resultT = await taskT;
                resultT.Block.Dispose();
                break;
            case ScanTermination.Fails:
                gateT.Release();
                await Assert.ThrowsExactlyAsync<IOException>(() => taskT);
                break;
            case ScanTermination.Cancels:
                await cancellationT.CancelAsync();
                gateT.Release();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => taskT);
                break;
        }

        Assert.AreEqual(4, allowanceU.Count, "Scan U receives the full allowance once scan T finishes.");
        gateU.Release();
        var resultU = await taskU;
        resultU.Block.Dispose();

        Assert.AreEqual(0, allocator.RunningScanCount, "All registrations must be released after scans end.");
        Assert.AreEqual(0, allocator.QueuedScanCount);
    }

    [TestMethod]
    [DataRow(-1L, 0u)]
    [DataRow(4294967296L, uint.MaxValue)]
    public async Task Produce_ProgressFlowsToIndexRequestWithSaturatedCounts(long count, uint expected)
    {
        var samples = new List<IndexScanProgress>();
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, reporting, _, _, _) =>
        {
            reporting.Progress!.Report(new BlockWriteProgress(count, 0, count, null, BlockScanPhase.Parsing));
            return Batches();
        }));
        using var block = (await ProducerOf(producer)(
            Request(progress: new DirectProgress<IndexScanProgress>(samples.Add)),
            TestContext.CancellationToken)).Block;
        var parsing = samples.Single(sample => sample.Phase == IndexScanPhase.ParsingMft);
        Assert.AreEqual(expected, parsing.WrittenRowCount);
        Assert.AreEqual(expected, parsing.ExpectedRowCount);
        Assert.IsTrue(samples.All(sample => sample.Drive == new VolumeKey('T')));
        Assert.AreEqual(IndexScanPhase.Transferring, samples[^1].Phase);
        Assert.IsNull(samples[^1].ExpectedRowCount);
    }

    [TestMethod]
    public async Task Produce_CancellationIsOperationCanceledAndLeavesNoBlock()
    {
        using var cancellation = new CancellationTokenSource();
        var request = Request();
        var producer = new LocalMftBlockProducer(TimeProvider.System, null,
            Scripted(scan: CancellingScan(cancellation)));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => ProducerOf(producer)(request, cancellation.Token));

        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    static MftRecordBatchSource CancellingScan(CancellationTokenSource cancellation) =>
        (_, _, _, _, _, token) => Cancelling(cancellation, token);

    static IEnumerable<IReadOnlyList<MftRecord>> Cancelling(CancellationTokenSource cancellation,
        CancellationToken token)
    {
        yield return [Record(5, ".", 3)];
        cancellation.Cancel();
        token.ThrowIfCancellationRequested();
    }

    [TestMethod]
    public async Task Produce_AlreadyCancelledNeverQueriesTheVolume()
    {
        var queried = false;
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(_ =>
        {
            queried = true;
            return Volume;
        }));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => ProducerOf(producer)(Request(), new CancellationToken(true)));

        Assert.IsFalse(queried);
    }

    [TestMethod]
    public async Task Produce_ScanFailureDisposesTheBlockAndPropagates()
    {
        const string message = @"Unable to open volume \\.\T:";
        var request = Request();
        var producer = new LocalMftBlockProducer(TimeProvider.System, null,
            Scripted(scan: (_, _, _, _, _, _) => throw new IOException(message)));

        var failure = await Assert.ThrowsExactlyAsync<IOException>(
            () => ProducerOf(producer)(request, TestContext.CancellationToken));

        Assert.AreEqual(message, failure.Message);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_ABlockWithNoRowsFailsValidationAndIsDisposed()
    {
        var request = Request();
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, _, _, _, _) => []));

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ProducerOf(producer)(request, TestContext.CancellationToken));

        Assert.Contains("RowCount", failure.Message);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Index_FullSyntheticScanYieldsSearchableEntriesAndNoWatch()
    {
        var reports = new List<IndexScanProgress>();
        var source = new LocalMftBlockProducer(TimeProvider.System, null, Scripted()).CreateIndexSource();

        await using var index = await FileIndex.OpenAsync(
            Options(source, new DirectProgress<IndexScanProgress>(reports.Add)),
            TestContext.CancellationToken);

        var names = index.Search(new SearchQuery("file.txt"), TestContext.CancellationToken).Select(entry => entry.Name).ToArray();
        Assert.AreSequenceEqual(["file.txt"], names);
        var status = index.Status;
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
        Assert.IsFalse(status.Watch.IsSupported);
        Assert.IsFalse(status.Watch.IsRequested);
        Assert.IsNotEmpty(reports);
    }

    [TestMethod]
    public async Task Index_WatchStartAndCatchUpAreNotApplicableThroughTheNoWatchGate()
    {
        var source = new LocalMftBlockProducer(TimeProvider.System, null, Scripted()).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), TestContext.CancellationToken);
        await DriveResultAssertions.NotApplicableAsync(() => index.StartWatchingAsync(CancellationToken.None));
        await DriveResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(CancellationToken.None));

        Assert.IsFalse(index.Status.Watch.IsSupported);
        Assert.IsFalse(index.Status.Watch.IsRequested);
    }

    [TestMethod]
    public async Task Index_NonWindowsSeamSettlesProducerFailedWithTheSeamMessage()
    {
        const string message = "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).";
        var source = new LocalMftBlockProducer(TimeProvider.System, null,
            Scripted(_ => throw new PlatformNotSupportedException(message))).CreateIndexSource();

        await using var index = await FileIndex.OpenAsync(Options(source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.Contains(message, status.Failure?.Message!);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void LiveSeams_OnANonWindowsHostRefuseTheVolumeQuery()
    {
        Assert.ThrowsExactly<PlatformNotSupportedException>(
            () => LiveVolumeAccess.Instance.QueryVolumeInformation('T'));
    }
}
