using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The public block facade of MFTLibTestExtensions: every member is exercised here, through a real
///     <see cref="FileIndex" /> open, <see cref="CacheDirectory.InspectCached(string, IReadOnlySet{char}?)" /> or a direct
///     <see cref="BlockFile" /> read, so the block it writes is judged by the production reader.
/// </summary>
[TestClass]
public partial class SyntheticBlockTests
{
    const uint Serial = 0x1234;
    static readonly DateTime Moment = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
    static readonly SyntheticJournalCursor Cursor = new(7, 4096);

    OwnedIndexDirectories _directories = null!;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    string CacheDirectoryPath => _directories.CacheDirectory;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        Directory.CreateDirectory(_directories.TreeRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    static SyntheticBlockOptions Options() => new() { JournalCursor = Cursor, CompletedUtc = Moment };

    static SyntheticRow[] SampleRows() =>
    [
        new SyntheticRow(0, "$MFT", 0) { ModifiedUtc = Moment },
        new SyntheticRow(5, ".", 5) { IsDirectory = true, ModifiedUtc = Moment },
        new SyntheticRow(6, "docs", 5) { IsDirectory = true, ModifiedUtc = Moment },
        new SyntheticRow(7, "report.txt", 6)
        {
            Size = 1234,
            Attributes = FileAttributes.ReadOnly | FileAttributes.Archive,
            ModifiedUtc = Moment.AddHours(1),
            SequenceNumber = 9
        },
        new SyntheticRow(8, "unsized.bin", 6) { Size = null, ModifiedUtc = Moment },
        new SyntheticRow(9, "gone.tmp", 6) { IsTombstone = true, Size = 5, ModifiedUtc = Moment }
    ];

    string Seed(IEnumerable<SyntheticRow>? rows = null, SyntheticBlockOptions? options = null) =>
        SyntheticBlock.WriteCached(CacheDirectoryPath, 'T', Serial, options ?? Options(), rows ?? SampleRows());

    FileIndexOptions CacheOnly() => new()
    {
        Drives = [new IndexedDrive('T', _directories.TreeRoot, Serial)],
        CacheDirectory = CacheDirectoryPath,
        ProducerPolicy = ProducerPolicy.Mft,
        MftSource = new MftIndexSource((_, _) => throw new InvalidOperationException("the cache must satisfy the open")),
        InitialOpenCacheOnly = true
    };

    FileIndexOptions Scanning(SyntheticMftProducer producer, CacheTag cacheTag = default) => new()
    {
        Drives = [new IndexedDrive('T', _directories.TreeRoot, Serial)],
        CacheDirectory = CacheDirectoryPath,
        ProducerPolicy = ProducerPolicy.Mft,
        MftSource = new MftIndexSource(producer.Producer),
        CacheTag = cacheTag
    };

    static SyntheticMftProducer SampleProducer() => new(_ => SampleRows()) { JournalCursor = Cursor, CompletedUtc = Moment };

    [TestMethod]
    public void MaximumPathDepth_IsTheDepthTheIndexResolves()
    {
        Assert.AreEqual(BlockLayout.MaximumPathDepth, SyntheticBlock.MaximumPathDepth);
    }

    [TestMethod]
    public void CachedPath_IsTheCanonicalBlockFileInTheCacheDirectory()
    {
        var path = SyntheticBlock.CachedPath(CacheDirectoryPath, 'T', Serial);

        Assert.AreEqual(Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', Serial)), path);
    }

    [TestMethod]
    public void WriteCached_ThenReadRows_RoundTripsEveryColumn()
    {
        var path = Seed();

        Assert.AreEqual(SyntheticBlock.CachedPath(CacheDirectoryPath, 'T', Serial), path);
        CollectionAssert.AreEqual(SampleRows(), SyntheticBlock.ReadRows(path, Serial).ToArray());
    }

    [TestMethod]
    public void WriteCached_PlansCapacityFromTheRowsSoManyRowsFit()
    {
        var rows = new List<SyntheticRow> { new(5, ".", 5) { IsDirectory = true } };
        rows.AddRange(Enumerable.Range(6, 4000).Select(index => new SyntheticRow((uint)index, $"file-{index}.dat", 5)));

        var path = Seed(rows);

        Assert.AreEqual(rows.Count, SyntheticBlock.ReadRows(path, Serial).Count);
    }

    [TestMethod]
    public async Task WriteCached_BlockWarmStartsAnIndexThatFindsItsRows()
    {
        Seed();

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var drive = index.Drives.Single();
        Assert.AreEqual(BlockSource.WarmStartedFromCache, drive.BlockSource);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.AreEqual(Moment, drive.ScanTimestamp);
        Assert.AreEqual(10u, index.HeaderOf().RowCount);
        Assert.AreEqual(5u, drive.LiveRowCount);
        var report = index.Search(new SearchQuery("report.txt", NameMatchMode.Exact)).Single();
        Assert.AreEqual(1234, report.Size);
        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Archive, report.Attributes);
        Assert.IsFalse(index.Search(new SearchQuery("unsized.bin", NameMatchMode.Exact)).Single().SizeKnown);
    }

    [TestMethod]
    public void WriteCached_EnumerationBlockWithRootRowZeroInspectsAsEnumeration()
    {
        Seed(
            [
                new SyntheticRow(0, "root", 0) { IsDirectory = true },
                new SyntheticRow(1, "child.txt", 0)
            ],
            Options() with { ProducerKind = ProducerKind.Enumeration, RootRow = 0 });

        var status = CacheDirectory.InspectCached(CacheDirectoryPath).Single();

        Assert.AreEqual(CachedBlockAvailability.Available, status.Availability);
        Assert.AreEqual(ProducerKind.Enumeration, status.ProducerKind);
    }

    [TestMethod]
    public void WriteCached_StampsTheCacheTagAndJournalCursor()
    {
        var tag = new CacheTag("SYNT", 3);
        var path = Seed(options: Options() with { CacheTag = tag });

        Assert.AreEqual(tag, CacheDirectory.InspectCached(CacheDirectoryPath).Single().CacheTag);
        using var block = BlockFile.Open(path, Serial, out _)!;
        Assert.AreEqual(Cursor.JournalIdentifier, block.Header.UsnJournalId);
        Assert.AreEqual(Cursor.NextUpdateSequenceNumber, block.Header.UsnNextUsn);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "missing-root-row")]
    [DataRow(true, DisplayName = "#360-empty-rows")]
    public void WriteCached_RootRowMissingFromTheRows_YieldsABlockTheReaderRejects(bool emptyRows)
    {
        Seed(emptyRows ? [] : [new SyntheticRow(0, "$MFT", 0)]);

        var status = CacheDirectory.InspectCached(CacheDirectoryPath).Single();

        Assert.AreEqual(CachedBlockAvailability.Invalid, status.Availability);
    }

    [TestMethod]
    public async Task Edit_WriteRowAndSetAttributes_AreSeenByAnIndexOpenedAfterwards()
    {
        var path = Seed();
        SyntheticBlock.Edit(path, Serial, editor =>
        {
            Assert.AreEqual(10u, editor.RowCount);
            Assert.AreEqual("report.txt", editor.ReadRow(7).Name);
            editor.WriteRow(new SyntheticRow(7, "renamed.txt", 6) { Size = 77, ModifiedUtc = Moment });
            editor.WriteRow(new SyntheticRow(10, "added.txt", 6) { Size = 1, ModifiedUtc = Moment });
            editor.SetAttributes(6, FileAttributes.Hidden | FileAttributes.Directory);
        });

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.AreEqual(0, index.Search(new SearchQuery("report.txt", NameMatchMode.Exact)).Count);
        Assert.AreEqual(77, index.Search(new SearchQuery("renamed.txt", NameMatchMode.Exact)).Single().Size);
        Assert.AreEqual(1, index.Search(new SearchQuery("added.txt", NameMatchMode.Exact)).Single().Size);
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.Directory, index.Search(new SearchQuery("docs", NameMatchMode.Exact)).Single().Attributes);
    }

    [TestMethod]
    public async Task Edit_MarkTombstone_LowersTheLiveRowCountTheIndexReports()
    {
        var path = Seed();
        SyntheticBlock.Edit(path, Serial, editor => editor.MarkTombstone(7));
        Assert.IsTrue(SyntheticBlock.ReadRows(path, Serial).Single(row => row.Row == 7).IsTombstone);

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.AreEqual(4u, index.Drives.Single().LiveRowCount);
    }

    [TestMethod]
    public async Task Edit_MarkCompactionNeeded_MakesTheDriveStale()
    {
        var path = Seed();
        SyntheticBlock.Edit(path, Serial, editor => editor.MarkCompactionNeeded());

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var drive = index.Drives.Single();
        Assert.IsTrue(drive.CompactionNeeded);
        Assert.AreEqual(DriveState.Stale, drive.State);
    }

    [TestMethod]
    public async Task Edit_SetJournalCursorAndComplete_ReplaceTheCursorAndTheScanTimestamp()
    {
        var path = Seed();
        var later = Moment.AddDays(1);
        SyntheticBlock.Edit(path, Serial, editor =>
        {
            editor.SetJournalCursor(new SyntheticJournalCursor(8, 9000));
            editor.Complete(later);
        });

        await using (var index = await FileIndex.OpenAsync(CacheOnly(), Token))
        {
            Assert.AreEqual(later, index.Drives.Single().ScanTimestamp);
        }

        using var block = BlockFile.Open(path, Serial, out _)!;
        Assert.AreEqual(8UL, block.Header.UsnJournalId);
        Assert.AreEqual(9000L, block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task Edit_CorruptNamePool_MakesTheReaderRejectTheBlockAndTheIndexRescan()
    {
        var path = Seed();
        SyntheticBlock.Edit(path, Serial, editor => editor.CorruptNamePool());

        var status = CacheDirectory.InspectCached(CacheDirectoryPath).Single();
        Assert.AreEqual(CachedBlockAvailability.Invalid, status.Availability);
        Assert.AreEqual(BlockValidationResult.InvalidNameDescriptor, status.Validation);
        var producer = SampleProducer();
        await using var index = await FileIndex.OpenAsync(Scanning(producer), Token);
        var drive = index.Drives.Single();
        Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
    }

    [TestMethod]
    public void Edit_EditorUsedAfterTheEditReturned_Throws()
    {
        var path = Seed();
        SyntheticBlockEditor? kept = null;
        SyntheticBlock.Edit(path, Serial, editor => kept = editor);

        Assert.ThrowsException<InvalidOperationException>(() => _ = kept!.RowCount);
        Assert.ThrowsException<InvalidOperationException>(() => kept!.ReadRow(7));
        Assert.ThrowsException<InvalidOperationException>(() => kept!.WriteRow(new SyntheticRow(7, "x", 6)));
        Assert.ThrowsException<InvalidOperationException>(() => kept!.CorruptNamePool());
        Assert.ThrowsException<InvalidOperationException>(
            () => kept!.SetCacheTag(new CacheTag("TEST", 7)));
    }

    [TestMethod]
    public void Edit_ARowNotInUse_ThrowsAndRowMutatorsRejectGaps()
    {
        var path = Seed();

        SyntheticBlock.Edit(path, Serial, editor =>
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => editor.ReadRow(3));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => editor.SetAttributes(500, 0));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => editor.MarkTombstone(4));
            Assert.ThrowsException<InvalidOperationException>(() => editor.WriteRow(new SyntheticRow(100_000, "overflow", 5)));
        });
    }

    [TestMethod]
    public void Edit_MissingOrInvalidBlock_ThrowsNamingTheProblem()
    {
        var missing = SyntheticBlock.CachedPath(CacheDirectoryPath, 'T', Serial);
        var absent = Assert.ThrowsException<InvalidOperationException>(
            () => SyntheticBlock.Edit(missing, Serial, _ => { }));
        StringAssert.Contains(absent.Message, "does not exist");

        var path = Seed();
        var wrongSerial = Assert.ThrowsException<InvalidOperationException>(
            () => SyntheticBlock.ReadRows(path, Serial + 1));
        StringAssert.Contains(wrongSerial.Message, nameof(BlockValidationResult.WrongVolumeSerial));

        var oversized = Assert.ThrowsException<InvalidOperationException>(
            () => Seed([new SyntheticRow(5, new string('x', 32768), 5)]));
        StringAssert.Contains(oversized.Message, "does not fit");
    }

    [TestMethod]
    public void ReadHeader_ReturnsTheProducerRowCountAndTagTheBlockWasWrittenWith()
    {
        var tag = new CacheTag("SYNT", 3);
        var path = Seed(options: Options() with { CacheTag = tag });

        var header = SyntheticBlock.ReadHeader(path, Serial);

        Assert.AreEqual(ProducerKind.Mft, header.ProducerKind);
        Assert.AreEqual(10u, header.RowCount, "the highest used slot plus one, tombstones included");
        Assert.AreEqual(tag, header.CacheTag);
        Assert.AreEqual(Moment, header.CompletedUtc);
    }

    [TestMethod]
    public void ReadHeader_OfABlockTheProductionReaderRejects_ThrowsWithTheReason()
    {
        var path = Seed();
        var bytes = File.ReadAllBytes(path);
        bytes[0] = 0;
        File.WriteAllBytes(path, bytes);

        var failure = Assert.ThrowsException<InvalidOperationException>(() => SyntheticBlock.ReadHeader(path, Serial));

        StringAssert.Contains(failure.Message, nameof(BlockValidationResult.WrongMagic));
        Assert.ThrowsException<InvalidOperationException>(
            () => SyntheticBlock.ReadHeader(Path.Combine(CacheDirectoryPath, "absent.mlix"), Serial));
    }

    [TestMethod]
    public async Task EveryOperation_OnASlotAnOpenIndexOwns_ThrowsInsteadOfRacingIt()
    {
        var path = Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.ThrowsException<InvalidOperationException>(() => SyntheticBlock.Edit(path, Serial, _ => { }));
        Assert.ThrowsException<InvalidOperationException>(() => SyntheticBlock.ReadRows(path, Serial));
        Assert.ThrowsException<InvalidOperationException>(() => SyntheticBlock.ReadHeader(path, Serial));
        Assert.ThrowsException<InvalidOperationException>(() => Seed());
    }

    [TestMethod]
    public async Task Producer_WritesItsRowsThroughTheRealWriterAndReportsItsSettings()
    {
        var tag = new CacheTag("SYNT", 1);
        var producer = SampleProducer();
        producer.SkippedRecordCount = 3;

        await using (var index = await FileIndex.OpenAsync(Scanning(producer, tag), Token))
        {
            var drive = index.Drives.Single();
            Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
            Assert.AreEqual(3, drive.SkippedRecordCount);
            Assert.AreEqual(Moment, drive.ScanTimestamp);
            Assert.AreEqual(1234, index.Search(new SearchQuery("report.txt", NameMatchMode.Exact)).Single().Size);
        }

        var status = CacheDirectory.InspectCached(CacheDirectoryPath).Single();
        Assert.AreEqual(tag, status.CacheTag, "the producer copies the request's cache tag");
        using var block = BlockFile.Open(SyntheticBlock.CachedPath(CacheDirectoryPath, 'T', Serial), Serial, out _)!;
        Assert.AreEqual(Cursor.JournalIdentifier, block.Header.UsnJournalId);
        Assert.AreEqual(Cursor.NextUpdateSequenceNumber, block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task Producer_CountsEveryProductionIncludingARescan()
    {
        var producer = SampleProducer();
        await using var index = await FileIndex.OpenAsync(Scanning(producer), Token);
        CollectionAssert.AreEqual(new[] { 'T' }, producer.ProducedDrives.ToArray());

        await index.RescanAsync('T', Token);

        CollectionAssert.AreEqual(new[] { 'T', 'T' }, producer.ProducedDrives.ToArray());
    }

    [TestMethod]
    public async Task Producer_BeforeProduceAsync_HoldsTheScanUntilReleased()
    {
        var producer = SampleProducer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        producer.BeforeProduceAsync = async (letter, cancellationToken) =>
        {
            Assert.AreEqual('T', letter);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var opening = FileIndex.OpenAsync(Scanning(producer), Token);
        await entered.Task.WaitAsync(Token);

        Assert.IsFalse(opening.IsCompleted, "the held scan keeps the open pending");
        Assert.AreEqual(1, producer.ProducedDrives.Count, "a held production is already counted");
        release.SetResult();
        await using var index = await opening;
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "retry-on-loss")]
    [DataRow(true, DisplayName = "#360-throw-disposes-block")]
    public async Task Producer_CatchUpLoss_SurfacesAsTheDrivesCheckpointLossAndTriggersARetry(bool throwOnEvaluation)
    {
        var loss = WatchDeduplicationTestSupport.StandardCatchUpLoss('T');
        var producer = SampleProducer();
        if (throwOnEvaluation)
        {
            producer.CatchUpLoss = _ => throw new InvalidOperationException("catch-up evaluation failed");
            await using var failedIndex = await FileIndex.OpenAsync(Scanning(producer), Token);
            var failedDrive = failedIndex.Drives.Single();
            Assert.AreEqual(DriveState.Failed, failedDrive.State);
            Assert.AreEqual(DriveFailureKind.ProducerFailed, failedDrive.FailureKind);
            StringAssert.Contains(failedDrive.MftProducerFailureMessage, "catch-up evaluation failed");
            return;
        }

        var reported = 0;
        producer.CatchUpLoss = _ => reported++ == 0 ? loss : null;

        await using var index = await FileIndex.OpenAsync(Scanning(producer), Token);

        var drive = index.Drives.Single();
        Assert.AreEqual(loss, drive.CheckpointLoss);
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(2, producer.ProducedDrives.Count, "the lost catch-up is scanned again");
    }

    [TestMethod]
    public void Producer_NullRowSource_IsRejected()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new SyntheticMftProducer(null!));
    }
}
