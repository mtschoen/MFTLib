using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Through a real <see cref="FileIndex" /> over a seeded cache block: a journal event never replaces a scanned
///     last-write time with its own time, invalidated and journal-created metadata reads as unknown, a last-write
///     range query excludes unknown rows, <see cref="IndexRow" /> exposes the same columns as
///     <see cref="FileEntry" />, and the flags survive a cache reopen and are replaced by a rescan's observations.
///     The index has no watch and no live journal: batches are applied through the test extensions.
/// </summary>
[TestClass]
public sealed class MetadataFreshnessIndexTests : IDisposable
{
    const uint Serial = 0x2468;
    static readonly DateTime ScannedMoment = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime OtherScannedMoment = new(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    static readonly DateTime EventMoment = new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
    static readonly SyntheticJournalCursor Cursor = new(7, 4096);

    IndexTreeFixture _fixture = null!;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture(seedDocuments: false);

    public void Dispose() => _fixture.Dispose();

    static SyntheticRow[] Rows() =>
    [
        new SyntheticRow(5, ".", 5) { IsDirectory = true, Attributes = FileAttributes.Directory, LastWriteTime = ScannedMoment },
        new SyntheticRow(6, "documents", 5) { IsDirectory = true, Attributes = FileAttributes.Directory, LastWriteTime = ScannedMoment },
        new SyntheticRow(7, "report.txt", 6)
        {
            Size = 1234,
            Attributes = FileAttributes.Archive,
            LastWriteTime = ScannedMoment
        },
        new SyntheticRow(8, "stamped-by-journal.txt", 6) { Size = 5, IsLastWriteTimeUnknown = true },
        new SyntheticRow(9, "other.txt", 6) { Size = 77, LastWriteTime = OtherScannedMoment }
    ];

    FileIndexOptions CacheOnly() =>
        _fixture.Options(volumeSerial: Serial, producer: ProducerKind.Mft, cacheMode: CacheMode.ExistingOnly) with
        {
            MftSource = new MftIndexSource((_, _) => throw new InvalidOperationException("the cache must satisfy the open"))
        };

    string Seed() =>
        SyntheticBlock.WriteCached(_fixture.CacheDirectory, 'T', Serial,
            new SyntheticBlockOptions { JournalCursor = Cursor, ScanTimestamp = ScannedMoment }, Rows());

    static SyntheticJournalRecord Record(ulong recordNumber, ulong parent, string name, SyntheticJournalReason reason,
        FileAttributes attributes = FileAttributes.Archive) =>
        new()
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parent,
            UpdateSequenceNumber = 100 + (long)recordNumber,
            FileName = name,
            Reason = reason,
            FileAttributes = attributes,
            Timestamp = EventMoment
        };

    static FileEntry Named(FileIndex index, string name) =>
        index.Search(new SearchQuery(name, NameMatchMode.Exact)).Single();

    [TestMethod]
    public async Task SeededUnknownRow_ReadsMinValueAndIsExcludedFromEveryBoundedQuery()
    {
        Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var unknown = Named(index, "stamped-by-journal.txt");
        Assert.IsFalse(unknown.IsLastWriteTimeKnown);
        Assert.AreEqual(DateTime.MinValue, unknown.LastWriteTime);

        var farFuture = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var upper = index.Search(new SearchQuery(null, IsDirectory: false, LastWriteTimeMaximum: farFuture));
        CollectionAssert.AreEquivalent(new[] { "report.txt", "other.txt" }, upper.Select(entry => entry.Name).ToArray());

        var lower = index.Search(new SearchQuery(null, IsDirectory: false, LastWriteTimeMinimum: DateTime.MinValue));
        CollectionAssert.AreEquivalent(new[] { "report.txt", "other.txt" }, lower.Select(entry => entry.Name).ToArray());

        var unbounded = index.Search(new SearchQuery(null, IsDirectory: false));
        Assert.AreEqual(3, unbounded.Count, "Without a last-write bound the unknown row is an ordinary row.");
    }

    [TestMethod]
    public async Task Rename_KeepsTheScannedLastWriteTimeSoAnEventDateQueryDoesNotFindIt()
    {
        Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var changes = FileIndexTestAccess.ApplyJournalEntries(index, 'T',
            [Record(7, 6, "renamed.txt", SyntheticJournalReason.RenameNewName)], Cursor);

        Assert.AreEqual(FileChangeKind.Renamed, changes.Single().Kind);
        var renamed = Named(index, "renamed.txt");
        Assert.IsTrue(renamed.IsLastWriteTimeKnown);
        Assert.AreEqual(ScannedMoment, renamed.LastWriteTime);
        Assert.AreEqual(1234, renamed.Size);
        Assert.AreEqual(EventMoment, changes.Single().Timestamp);

        var foundByEventDate = index.Search(new SearchQuery("renamed", LastWriteTimeMinimum: EventMoment));
        Assert.AreEqual(0, foundByEventDate.Count);
    }

    [TestMethod]
    public async Task ContentChange_MarksTheEntryUnknownAndTheRangeQueryExcludesIt()
    {
        Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        FileIndexTestAccess.ApplyJournalEntries(index, 'T',
            [Record(7, 6, "report.txt", SyntheticJournalReason.DataExtend)], Cursor);

        var changed = Named(index, "report.txt");
        Assert.IsFalse(changed.IsSizeKnown);
        Assert.IsFalse(changed.IsLastWriteTimeKnown);
        Assert.AreEqual(DateTime.MinValue, changed.LastWriteTime);
        var ranged = index.Search(new SearchQuery("report", LastWriteTimeMinimum: ScannedMoment,
            LastWriteTimeMaximum: EventMoment));
        Assert.AreEqual(0, ranged.Count);
    }

    [TestMethod]
    public async Task BasicInfoChange_MarksOnlyTheLastWriteTimeUnknown()
    {
        Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        FileIndexTestAccess.ApplyJournalEntries(index, 'T',
            [Record(7, 6, "report.txt", SyntheticJournalReason.BasicInformationChange, FileAttributes.Hidden)], Cursor);

        var changed = Named(index, "report.txt");
        Assert.IsFalse(changed.IsLastWriteTimeKnown);
        Assert.IsTrue(changed.IsSizeKnown);
        Assert.AreEqual(1234, changed.Size);
        Assert.AreEqual(FileAttributes.Hidden, changed.Attributes);
    }

    [TestMethod]
    public async Task JournalCreatedEntry_IsUnknownForSizeAndLastWriteTime()
    {
        Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        var change = FileIndexTestAccess.ApplyJournalEntries(index, 'T',
            [Record(10, 6, "created.txt", SyntheticJournalReason.FileCreate)], Cursor).Single();

        Assert.AreEqual(FileChangeKind.Created, change.Kind);
        Assert.IsFalse(change.Entry.IsSizeKnown);
        Assert.IsFalse(change.Entry.IsLastWriteTimeKnown);
        Assert.AreEqual(DateTime.MinValue, change.Entry.LastWriteTime);
        Assert.AreEqual(0, index.Search(new SearchQuery("created", LastWriteTimeMaximum: EventMoment)).Count);
    }

    [TestMethod]
    public async Task EnumerateRows_ExposesEveryFileEntryColumnWithoutMaterializingAnEntry()
    {
        Seed();
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);
        FileIndexTestAccess.ApplyJournalEntries(index, 'T',
        [
            Record(7, 6, "renamed.txt", SyntheticJournalReason.RenameNewName, FileAttributes.ReadOnly),
            Record(9, 6, "other.txt", SyntheticJournalReason.BasicInformationChange, FileAttributes.Hidden),
            Record(10, 6, "created.txt", SyntheticJournalReason.FileCreate)
        ], Cursor);

        var compared = 0;
        foreach (var row in index.EnumerateRows(new SearchQuery(null, IncludeDeleted: true), Token))
        {
            var entry = row.ToEntry();
            Assert.AreEqual(entry.Size, row.Size, entry.Name);
            Assert.AreEqual(entry.IsSizeKnown, row.IsSizeKnown, entry.Name);
            Assert.AreEqual(entry.LastWriteTime, row.LastWriteTime, entry.Name);
            Assert.AreEqual(entry.IsLastWriteTimeKnown, row.IsLastWriteTimeKnown, entry.Name);
            Assert.AreEqual(entry.Attributes, row.Attributes, entry.Name);
            Assert.AreEqual(entry.IsDirectory, row.IsDirectory, entry.Name);
            Assert.AreEqual(entry.IsDeleted, row.IsDeleted, entry.Name);
            compared++;
        }

        Assert.AreEqual(6, compared, "The root, the directory and four files.");
        var renamed = Named(index, "renamed.txt");
        Assert.AreEqual(FileAttributes.ReadOnly, renamed.Attributes);
        Assert.AreEqual(ScannedMoment, renamed.LastWriteTime);
    }

    [TestMethod]
    public async Task CacheReopen_KeepsTheFlagsAndValuesTheJournalLeft()
    {
        var blockPath = Seed();
        await using (var first = await FileIndex.OpenAsync(CacheOnly(), Token))
        {
            FileIndexTestAccess.ApplyJournalEntries(first, 'T',
            [
                Record(7, 6, "report.txt", SyntheticJournalReason.DataExtend),
                Record(9, 6, "other.txt", SyntheticJournalReason.RenameNewName)
            ], Cursor);
        }

        var stored = SyntheticBlock.ReadRows(blockPath, Serial).ToDictionary(row => row.Row);
        Assert.IsTrue(stored[7].IsLastWriteTimeUnknown);
        Assert.IsNull(stored[7].Size);
        Assert.IsFalse(stored[9].IsLastWriteTimeUnknown);
        Assert.AreEqual(OtherScannedMoment, stored[9].LastWriteTime);

        await using var reopened = await FileIndex.OpenAsync(CacheOnly(), Token);
        var content = Named(reopened, "report.txt");
        Assert.IsFalse(content.IsLastWriteTimeKnown);
        Assert.IsFalse(content.IsSizeKnown);
        var renamed = Named(reopened, "other.txt");
        Assert.IsTrue(renamed.IsLastWriteTimeKnown);
        Assert.AreEqual(OtherScannedMoment, renamed.LastWriteTime);
    }

    [TestMethod]
    public async Task Rescan_ReplacesUnknownMetadataWithTheScansObservations()
    {
        var producer = new SyntheticMftProducer(_ => Rows().Where(row => row.Row != 8)
            .Append(new SyntheticRow(8, "stamped-by-journal.txt", 6) { Size = 5, LastWriteTime = ScannedMoment }))
        {
            JournalCursor = Cursor,
            ScanTimestamp = ScannedMoment
        };
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(volumeSerial: Serial, producer: ProducerKind.Mft) with
            {
                MftSource = new MftIndexSource(producer.Producer)
            }, Token);
        FileIndexTestAccess.ApplyJournalEntries(index, 'T',
        [
            Record(7, 6, "report.txt", SyntheticJournalReason.DataExtend),
            Record(8, 6, "stamped-by-journal.txt", SyntheticJournalReason.BasicInformationChange)
        ], Cursor);
        Assert.IsFalse(Named(index, "report.txt").IsLastWriteTimeKnown);

        await index.RescanAsync(['T'], Token);

        var rescanned = Named(index, "report.txt");
        Assert.IsTrue(rescanned.IsLastWriteTimeKnown);
        Assert.IsTrue(rescanned.IsSizeKnown);
        Assert.AreEqual(ScannedMoment, rescanned.LastWriteTime);
        Assert.AreEqual(1234, rescanned.Size);
        Assert.IsTrue(Named(index, "stamped-by-journal.txt").IsLastWriteTimeKnown);
    }
}
