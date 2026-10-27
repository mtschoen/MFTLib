using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The public block facade of MFTLibTestExtensions: every member is exercised here, through a real
///     <see cref="FileIndex" /> open, <see cref="CacheDirectory.InspectCached" /> or a direct
///     <see cref="BlockFile" /> read, so the block it writes is judged by the production reader.
/// </summary>
[TestClass]
public sealed partial class SyntheticBlockTests : IndexTreeTestBase
{
    const uint Serial = 0x1234;
    static readonly DateTime Moment = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
    static readonly SyntheticJournalCursor Cursor = new(7, 4096);

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    protected override bool SeedDocuments => false;

    static SyntheticBlockOptions Options() => new() { JournalCursor = Cursor, ScanTimestamp = Moment };

    static SyntheticRow[] SampleRows() =>
    [
        new SyntheticRow(0, "$MFT", 0) { LastWriteTime = Moment },
        new SyntheticRow(5, ".", 5) { IsDirectory = true, LastWriteTime = Moment },
        new SyntheticRow(6, "docs", 5) { IsDirectory = true, LastWriteTime = Moment },
        new SyntheticRow(7, "report.txt", 6)
        {
            Size = 1234,
            Attributes = FileAttributes.ReadOnly | FileAttributes.Archive,
            LastWriteTime = Moment.AddHours(1),
            SequenceNumber = 9
        },
        new SyntheticRow(8, "unsized.bin", 6) { Size = null, LastWriteTime = Moment },
        new SyntheticRow(9, "gone.tmp", 6) { IsTombstone = true, Size = 5, LastWriteTime = Moment }
    ];

    string Seed(IEnumerable<SyntheticRow>? rows = null, SyntheticBlockOptions? options = null) =>
        SyntheticBlock.WriteCached(CacheDirectoryPath, new VolumeKey('T'), Serial, options ?? Options(), rows ?? SampleRows());

    FileIndexOptions CacheOnly(bool enumeration = false) =>
        Fixture.Options(volumeSerial: Serial,
            producer: enumeration ? ProducerKind.Enumeration : ProducerKind.Mft,
            cacheMode: CacheMode.ExistingOnly) with
        {
            MftSource = new MftIndexSource((_, _) => throw new InvalidOperationException("the cache must satisfy the open"))
        };

    FileIndexOptions Scanning(SyntheticMftProducer producer, CacheTag cacheTag = default) =>
        Fixture.Options(volumeSerial: Serial, producer: ProducerKind.Mft) with
        {
            MftSource = new MftIndexSource(producer.Producer),
            CacheTag = cacheTag
        };

    static SyntheticMftProducer SampleProducer() => new(_ => SampleRows()) { JournalCursor = Cursor, ScanTimestamp = Moment };

    static SyntheticMftProducer SampleProducer(Func<VolumeKey, CancellationToken, Task> beforeProduce) =>
        new(_ => SampleRows()) { JournalCursor = Cursor, ScanTimestamp = Moment, BeforeProduceAsync = beforeProduce };

    static SyntheticMftProducer SampleProducer(Func<VolumeKey, JournalCheckpointLoss?> catchUpLoss) =>
        new(_ => SampleRows()) { JournalCursor = Cursor, ScanTimestamp = Moment, CatchUpLoss = catchUpLoss };

    [TestMethod]
    public void MaximumPathDepth_IsTheDepthTheIndexResolves()
    {
        Assert.AreEqual(BlockLayout.MaximumPathDepth, SyntheticBlock.MaximumPathDepth);
    }

    [TestMethod]
    public void CachedPath_IsTheCanonicalBlockFileInTheCacheDirectory()
    {
        var path = SyntheticBlock.CachedPath(CacheDirectoryPath, new VolumeKey('T'), Serial);

        Assert.AreEqual(Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), Serial)), path);
    }

    [TestMethod]
    public void WriteCached_ThenReadRows_RoundTripsEveryColumn()
    {
        var path = Seed();

        Assert.AreEqual(SyntheticBlock.CachedPath(CacheDirectoryPath, new VolumeKey('T'), Serial), path);
        Assert.AreSequenceEqual(SampleRows(), [.. SyntheticBlock.ReadRows(path, Serial)]);
    }

    [TestMethod]
    public void WriteCached_PlansCapacityFromTheRowsSoManyRowsFit()
    {
        var rows = new List<SyntheticRow> { new(5, ".", 5) { IsDirectory = true } };
        rows.AddRange(Enumerable.Range(6, 4000).Select(index => new SyntheticRow((uint)index, $"file-{index}.dat", 5)));

        var path = Seed(rows);

        Assert.HasCount(rows.Count, SyntheticBlock.ReadRows(path, Serial));
    }

    [TestMethod]
    public async Task WriteCached_BlockWarmStartsAnIndexThatFindsItsRows()
    {
        Seed();

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var root = index.Status;
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, root.Block!.Origin);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.AreEqual(Moment, root.Block.ScanTimestamp);
        Assert.AreEqual(10u, index.HeaderOf().RowCount);
        Assert.AreEqual(5u, root.Block.LiveRowCount);
        var report = index.Search(new SearchQuery("report.txt", NameMatchMode.Exact), TestContext.CancellationToken).Single();
        Assert.AreEqual(1234, report.Size);
        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Archive, report.Attributes);
        Assert.IsFalse(index.Search(new SearchQuery("unsized.bin", NameMatchMode.Exact), TestContext.CancellationToken).Single().IsSizeKnown);
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
        Assert.AreEqual(Cursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(Cursor.NextUsn, block.Header.UsnNextUsn);
    }

    [TestMethod]
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
            editor.WriteRow(new SyntheticRow(7, "renamed.txt", 6) { Size = 77, LastWriteTime = Moment });
            editor.WriteRow(new SyntheticRow(10, "added.txt", 6) { Size = 1, LastWriteTime = Moment });
            editor.SetAttributes(6, FileAttributes.Hidden | FileAttributes.Directory);
        });

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.IsEmpty(index.Search(new SearchQuery("report.txt", NameMatchMode.Exact), TestContext.CancellationToken));
        Assert.AreEqual(77, index.Search(new SearchQuery("renamed.txt", NameMatchMode.Exact), TestContext.CancellationToken).Single().Size);
        Assert.AreEqual(1, index.Search(new SearchQuery("added.txt", NameMatchMode.Exact), TestContext.CancellationToken).Single().Size);
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.Directory, index.Search(new SearchQuery("docs", NameMatchMode.Exact), TestContext.CancellationToken).Single().Attributes);
    }

    [TestMethod]
    public async Task Edit_MarkTombstone_LowersTheLiveRowCountTheIndexReports()
    {
        var path = Seed();
        SyntheticBlock.Edit(path, Serial, editor => editor.MarkTombstone(7));
        Assert.IsTrue(SyntheticBlock.ReadRows(path, Serial).Single(row => row.Row == 7).IsTombstone);

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.AreEqual(4u, index.Status.Block!.LiveRowCount);
    }

    [TestMethod]
    public async Task Edit_MarkCapacityExhausted_ReportsReadyWithCapacityExhausted()
    {
        var path = Seed();
        SyntheticBlock.Edit(path, Serial, editor => editor.MarkCapacityExhausted());

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var root = index.Status;
        Assert.IsTrue(root.Block!.IsCapacityExhausted);
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.HasCount(1, index.Search(new SearchQuery("report.txt", NameMatchMode.Exact), TestContext.CancellationToken));
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
            Assert.AreEqual(later, index.Status.Block!.ScanTimestamp);
        }

        using var block = BlockFile.Open(path, Serial, out _)!;
        Assert.AreEqual(8UL, block.Header.UsnJournalIdentifier);
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
        var root = index.Status;
        Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin);
    }

    [TestMethod]
    public void Edit_EditorUsedAfterTheEditReturned_Throws()
    {
        var path = Seed();
        SyntheticBlockEditor? kept = null;
        SyntheticBlock.Edit(path, Serial, editor => kept = editor);

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = kept!.RowCount);
        Assert.ThrowsExactly<InvalidOperationException>(() => kept!.ReadRow(7));
        Assert.ThrowsExactly<InvalidOperationException>(() => kept!.WriteRow(new SyntheticRow(7, "x", 6)));
        Assert.ThrowsExactly<InvalidOperationException>(() => kept!.CorruptNamePool());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => kept!.SetCacheTag(new CacheTag("TEST", 7)));
    }

    [TestMethod]
    public void Edit_ARowNotInUse_ThrowsAndRowMutatorsRejectGaps()
    {
        var path = Seed();

        SyntheticBlock.Edit(path, Serial, editor =>
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.ReadRow(3));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.SetAttributes(500, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.MarkTombstone(4));
            Assert.ThrowsExactly<InvalidOperationException>(() => editor.WriteRow(new SyntheticRow(100_000, "overflow", 5)));
        });
    }

    [TestMethod]
    public void Edit_MissingOrInvalidBlock_ThrowsNamingTheProblem()
    {
        var missing = SyntheticBlock.CachedPath(CacheDirectoryPath, new VolumeKey('T'), Serial);
        var absent = Assert.ThrowsExactly<InvalidOperationException>(
            () => SyntheticBlock.Edit(missing, Serial, _ => { }));
        Assert.Contains("does not exist", absent.Message);

        var path = Seed();
        var wrongSerial = Assert.ThrowsExactly<InvalidOperationException>(
            () => SyntheticBlock.ReadRows(path, Serial + 1));
        Assert.Contains(nameof(BlockValidationResult.WrongVolumeSerial), wrongSerial.Message);

        var oversized = Assert.ThrowsExactly<InvalidOperationException>(
            () => Seed([new SyntheticRow(5, new string('x', 32768), 5)]));
        Assert.Contains("does not fit", oversized.Message);
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
        Assert.AreEqual(Moment, header.ScanTimestamp);
    }

    [TestMethod]
    public void ReadHeader_OfABlockTheProductionReaderRejects_ThrowsWithTheReason()
    {
        var path = Seed();
        var bytes = File.ReadAllBytes(path);
        bytes[0] = 0;
        File.WriteAllBytes(path, bytes);

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => SyntheticBlock.ReadHeader(path, Serial));

        Assert.Contains(nameof(BlockValidationResult.WrongMagic), failure.Message);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SyntheticBlock.ReadHeader(Path.Combine(CacheDirectoryPath, "absent.mlix"), Serial));
    }

    [TestMethod]
    public async Task EveryOperation_OnASlotAnOpenIndexOwns_ThrowsInsteadOfRacingIt()
    {
        var path = Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.ThrowsExactly<InvalidOperationException>(() => SyntheticBlock.Edit(path, Serial, _ => { }));
        Assert.ThrowsExactly<InvalidOperationException>(() => SyntheticBlock.ReadRows(path, Serial));
        Assert.ThrowsExactly<InvalidOperationException>(() => SyntheticBlock.ReadHeader(path, Serial));
        Assert.ThrowsExactly<InvalidOperationException>(() => Seed());
    }

    [TestMethod]
    public async Task Producer_WritesItsRowsThroughTheRealWriterAndReportsItsSettings()
    {
        var tag = new CacheTag("SYNT", 1);
        var producer = new SyntheticMftProducer(_ => SampleRows())
        { JournalCursor = Cursor, ScanTimestamp = Moment, SkippedRecordCount = 3 };

        await using (var index = await FileIndex.OpenAsync(Scanning(producer, tag), Token))
        {
            var root = index.Status;
            Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin);
            Assert.AreEqual(3, root.Block.SkippedRecordCount);
            Assert.AreEqual(Moment, root.Block.ScanTimestamp);
            Assert.AreEqual(1234, index.Search(new SearchQuery("report.txt", NameMatchMode.Exact), TestContext.CancellationToken).Single().Size);
        }

        var status = CacheDirectory.InspectCached(CacheDirectoryPath).Single();
        Assert.AreEqual(tag, status.CacheTag, "the producer copies the request's cache tag");
        using var block = BlockFile.Open(SyntheticBlock.CachedPath(CacheDirectoryPath, new VolumeKey('T'), Serial), Serial, out _)!;
        Assert.AreEqual(Cursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(Cursor.NextUsn, block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task Producer_CountsEveryProductionIncludingARescan()
    {
        var producer = SampleProducer();
        await using var index = await FileIndex.OpenAsync(Scanning(producer), Token);
        Assert.AreSequenceEqual(['T'], [.. producer.ProducedVolumes]);

        await index.RescanAsync(Token);

        Assert.AreSequenceEqual(['T', 'T'], [.. producer.ProducedVolumes]);
    }

    [TestMethod]
    public async Task Producer_BeforeProduceAsync_HoldsTheScanUntilReleased()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = SampleProducer(async (letter, cancellationToken) =>
        {
            Assert.AreEqual('T', letter);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });

        var opening = FileIndex.OpenAsync(Scanning(producer), Token);
        await entered.Task.WaitAsync(Token);

        Assert.IsFalse(opening.IsCompleted, "the held scan keeps the open pending");
        Assert.HasCount(1, producer.ProducedVolumes, "a held production is already counted");
        release.SetResult();
        await using var index = await opening;
        Assert.AreEqual(RootState.Ready, index.Status.State);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "retry-on-loss")]
    [DataRow(true, DisplayName = "#360-throw-disposes-block")]
    public async Task Producer_CatchUpLoss_SurfacesAsTheRootsCheckpointLossAndTriggersARetry(bool throwOnEvaluation)
    {
        var loss = CatchUpLossTestSupport.StandardCatchUpLoss('T');
        if (throwOnEvaluation)
        {
            var throwing = SampleProducer(
                _ => throw new InvalidOperationException("catch-up evaluation failed"));
            await using var failedIndex = await FileIndex.OpenAsync(Scanning(throwing), Token);
            var failedRoot = failedIndex.Status;
            Assert.AreEqual(RootState.Failed, failedRoot.State);
            Assert.AreEqual(RootFailureKind.ProducerFailed, failedRoot.Failure?.Kind);
            Assert.Contains("catch-up evaluation failed", failedRoot.Failure?.Message!);
            return;
        }

        var reported = 0;
        var producer = SampleProducer(_ => reported++ == 0 ? loss : null);

        await using var index = await FileIndex.OpenAsync(Scanning(producer), Token);

        var root = index.Status;
        Assert.AreEqual(loss, root.Watch.CheckpointLoss);
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.HasCount(2, producer.ProducedVolumes, "the lost catch-up is scanned again");
    }

    [TestMethod]
    public void Producer_NullRowSource_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SyntheticMftProducer(null!));
    }
}
