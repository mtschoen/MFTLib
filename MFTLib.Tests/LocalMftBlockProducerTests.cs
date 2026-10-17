using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class LocalMftBlockProducerTests
{
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);
    static readonly CacheTag RequestedTag = new("LOCL", 3);

    OwnedIndexDirectories _directories = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        Directory.CreateDirectory(_directories.TreeRoot);
        Directory.CreateDirectory(_directories.CacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name);
    }

    static IEnumerable<IReadOnlyList<MftRecord>> Batches() =>
        [[Record(5, ".", 3)], [Record(20, "file.txt"), Record(21, "other.md")]];

    static LocalMftBlockProducer.Seams Scripted(
        Func<string, NtfsVolumeInformation>? query = null, MftRecordBatchSource? scan = null,
        UsnJournalCursorQuery? queryCursor = null) => new(
        query ?? (_ => Volume), scan ?? ((_, _, _, _, _, _) => Batches()),
        queryCursor ?? (_ => new UsnJournalCursor(99, 2000)));

    MftBlockProduceRequest Request(bool deleteOnClose = true, IProgress<IndexScanProgress>? progress = null,
        char driveLetter = 'T') => new()
        {
            Drive = driveLetter,
            VolumeSerial = 123,
            BlockPath = Path.Combine(_directories.CacheDirectory,
            deleteOnClose ? $"{driveLetter}-temporary.mlix" : $"{driveLetter}-canonical.mlix"),
            DeleteOnClose = deleteOnClose,
            Progress = progress,
            CacheTag = RequestedTag
        };

    static MftBlockProducer ProducerOf(LocalMftBlockProducer producer) => producer.CreateIndexSource().Producer;

    FileIndexOptions Options(MftIndexSource source, IProgress<IndexScanProgress>? progress = null) => new()
    {
        Drives = [new IndexedDrive('T', _directories.TreeRoot, 0x0BADF00D)],
        CacheDirectory = _directories.CacheDirectory,
        CacheMode = CacheMode.Disabled,
        MftSource = source,
        ScanProgress = progress
    };

    [TestMethod]
    public void Factory_BuildsASourceWithNoWatchDumpOrUnavailableReason()
    {
        var source = MftIndexSources.FromLocalVolumes(new MftScanOptions());

        Assert.IsNull(source.WatchSource);
        Assert.IsNull(source.DumpIdentity);
        Assert.IsNull(source.UnavailableReason);
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
        var seams = Scripted(_ =>
        {
            calls++;
            return Volume;
        }, (_, _, _, _, _, _) =>
        {
            calls++;
            return Batches();
        });

        _ = new LocalMftBlockProducer(TimeProvider.System, null, seams).CreateIndexSource();

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
        var queried = new List<string>();
        var producer = new LocalMftBlockProducer(timeProvider, null, Scripted(drive =>
        {
            queried.Add(drive);
            return Volume;
        }, scan: (_, _, _, _, _, _) => TimedBatches()));

        var result = await ProducerOf(producer)(request, TestContext.CancellationTokenSource.Token);
        var block = result.Block;

        CollectionAssert.AreEqual(new[] { "T" }, queried);
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
            FileIndexTestAccess.WithTimeProvider(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', _directories.TreeRoot, 123, ProducerKind.Enumeration)],
                CacheMode = CacheMode.Disabled
            }, timeProvider), TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(timeProvider.GetUtcNow().UtcDateTime, enumeration.Drives.Single().Block!.ScanTimestamp);
    }

    [TestMethod]
    public async Task Produce_SaturatesSkippedRecordCount()
    {
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, _, _, recordsOmitted, _) =>
        {
            recordsOmitted((long)int.MaxValue + 1);
            return [new[] { Record(5, ".", 3) }];
        }));

        var result = await ProducerOf(producer)(Request(), TestContext.CancellationTokenSource.Token);
        using var block = result.Block;

        Assert.AreEqual(int.MaxValue, result.SkippedRecordCount);
        Assert.IsTrue(block.Header.IsComplete);
    }


    [DataTestMethod]
    [DataRow(false, DisplayName = "normal-reopen-unmoved-journal")]
    [DataRow(true, DisplayName = "cache-only-reopen-unmoved-journal")]
    public async Task Produce_CachePathBlockSurvivesDisposal(bool cacheOnly)
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(
            _ => new JournalWindow(99, 1000, 2000, 4096, 32768));
        var scanCalls = 0;
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref scanCalls);
            return Batches();
        }));
        var source = producer.CreateIndexSource();

        FileIndexOptions CachingOptions(bool isCacheOnly) => new()
        {
            Drives = [new IndexedDrive('T', _directories.TreeRoot, 0x0BADF00D)],
            CacheDirectory = _directories.CacheDirectory,
            CacheMode = isCacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent,
            MftSource = source
        };

        await using (var initialIndex = await FileIndex.OpenAsync(CachingOptions(isCacheOnly: false),
            TestContext.CancellationTokenSource.Token))
        {
            Assert.AreEqual(1, scanCalls);
            var initialDrive = initialIndex.Drives.Single();
            Assert.AreEqual(DriveState.Ready, initialDrive.State);
            Assert.AreEqual(BlockSource.ProducedByScan, initialDrive.Block!.Source);
            Assert.IsNull(initialDrive.Watch.CheckpointLoss);
        }

        var canonicalPath = Path.Combine(_directories.CacheDirectory,
            CacheDirectory.BlockFileName('T', 0x0BADF00D));
        Assert.IsTrue(File.Exists(canonicalPath));

        await using (var reopenedIndex = await FileIndex.OpenAsync(CachingOptions(isCacheOnly: cacheOnly),
            TestContext.CancellationTokenSource.Token))
        {
            Assert.AreEqual(1, scanCalls, "A cached block must not scan again on reopening.");
            var reopenedDrive = reopenedIndex.Drives.Single();
            Assert.AreEqual(DriveState.Ready, reopenedDrive.State);
            Assert.AreEqual(BlockSource.WarmStartedFromCache, reopenedDrive.Block!.Source);
            Assert.IsNull(reopenedDrive.Watch.CheckpointLoss);
        }
    }

    public enum ScanTermination
    {
        Completes,
        Fails,
        Cancels
    }

    [DataTestMethod]
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
            if (drive == "T")
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
        var taskT = ProducerOf(producer)(Request(driveLetter: 'T'), cancellationT.Token);
        await gateT.Entered.WaitAsync(TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(4, allowanceT!.Count, "A single running scan takes the full thread budget.");

        var taskU = ProducerOf(producer)(Request(driveLetter: 'U'), TestContext.CancellationTokenSource.Token);
        await gateU.Entered.WaitAsync(TestContext.CancellationTokenSource.Token);
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
                await Assert.ThrowsExceptionAsync<IOException>(() => taskT);
                break;
            case ScanTermination.Cancels:
                cancellationT.Cancel();
                gateT.Release();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => taskT);
                break;
        }

        Assert.AreEqual(4, allowanceU.Count, "Scan U receives the full allowance once scan T finishes.");
        gateU.Release();
        var resultU = await taskU;
        resultU.Block.Dispose();

        Assert.AreEqual(0, allocator.RunningScanCount, "All registrations must be released after scans end.");
        Assert.AreEqual(0, allocator.QueuedScanCount);
    }

    [DataTestMethod]
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
            TestContext.CancellationTokenSource.Token)).Block;
        var parsing = samples.Single(sample => sample.Phase == IndexScanPhase.ParsingMft);
        Assert.AreEqual(expected, parsing.WrittenRowCount);
        Assert.AreEqual(expected, parsing.ExpectedRowCount);
        Assert.IsTrue(samples.All(sample => sample.Drive == 'T'));
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

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
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

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => ProducerOf(producer)(Request(), new CancellationToken(true)));

        Assert.IsFalse(queried);
    }

    [TestMethod]
    public async Task Produce_ScanFailureDisposesTheBlockAndPropagates()
    {
        const string message = "Unable to open volume \\\\.\\T:";
        var request = Request();
        var producer = new LocalMftBlockProducer(TimeProvider.System, null,
            Scripted(scan: (_, _, _, _, _, _) => throw new IOException(message)));

        var failure = await Assert.ThrowsExceptionAsync<IOException>(
            () => ProducerOf(producer)(request, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(message, failure.Message);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_ABlockWithNoRowsFailsValidationAndIsDisposed()
    {
        var request = Request();
        var producer = new LocalMftBlockProducer(TimeProvider.System, null, Scripted(scan: (_, _, _, _, _, _) => []));

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => ProducerOf(producer)(request, TestContext.CancellationTokenSource.Token));

        StringAssert.Contains(failure.Message, "RowCount");
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Index_FullSyntheticScanYieldsSearchableEntriesAndNoWatch()
    {
        var reports = new List<IndexScanProgress>();
        var source = new LocalMftBlockProducer(TimeProvider.System, null, Scripted()).CreateIndexSource();

        await using var index = await FileIndex.OpenAsync(
            Options(source, new DirectProgress<IndexScanProgress>(reports.Add)),
            TestContext.CancellationTokenSource.Token);

        var names = index.Search(new SearchQuery("file.txt")).Select(entry => entry.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "file.txt" }, names);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockSource.ProducedByScan, status.Block!.Source);
        Assert.IsFalse(status.Watch.IsSupported);
        Assert.IsFalse(status.Watch.IsRequested);
        Assert.IsTrue(reports.Count > 0);
    }

    [TestMethod]
    public async Task Index_WatchStartAndCatchUpAreNotApplicableThroughTheNoWatchGate()
    {
        var source = new LocalMftBlockProducer(TimeProvider.System, null, Scripted()).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Options(source), TestContext.CancellationTokenSource.Token);
        await DriveResultAssertions.NotApplicableAsync(() => index.StartWatchingAsync(['t'], CancellationToken.None));
        await DriveResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(['t'], CancellationToken.None));

        Assert.IsFalse(index.Drives.Single().Watch.IsSupported);
        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
    }

    [TestMethod]
    public async Task Index_NonWindowsSeamSettlesProducerFailedWithTheSeamMessage()
    {
        const string message = "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).";
        var source = new LocalMftBlockProducer(TimeProvider.System, null,
            Scripted(_ => throw new PlatformNotSupportedException(message))).CreateIndexSource();

        await using var index = await FileIndex.OpenAsync(Options(source), TestContext.CancellationTokenSource.Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        StringAssert.Contains(status.Failure?.Message, message);
    }

    [TestMethod]
    public void LiveSeams_OnANonWindowsHostRefuseTheVolumeQuery()
    {
        Assert.AreEqual((UsnJournalCursorQuery)LiveVolumeSources.QueryCursor,
            LocalMftBlockProducer.Seams.Live.QueryCursor);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.ThrowsException<PlatformNotSupportedException>(
            () => LocalMftBlockProducer.Seams.Live.QueryVolumeInformation("T"));
    }
}
