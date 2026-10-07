using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A scan that imports freed records yields deleted rows: a verified one keeps its place in the tree, an
///     unverified one is detached, and only a search that asks for deleted rows returns either.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FreedRowsIndexTests
{
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);
    static readonly DateTime FixedMoment = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    OwnedIndexDirectories _directories = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        Directory.CreateDirectory(_directories.TreeRoot);
        Directory.CreateDirectory(_directories.CacheDirectory);
    }

    [TestCleanup]
    public void Cleanup() => _directories.Dispose();

    [TestMethod]
    public async Task VerifiedFreedRow_KeepsItsPlaceInTheTree()
    {
        await using var index = await OpenAsync(includeFreed: true);

        var entry = index.Search(new SearchQuery("trusted.txt", IncludeDeleted: true)).Single();

        Assert.IsTrue(entry.IsDeleted);
        Assert.AreEqual(Path.Combine(_directories.TreeRoot, "docs", "trusted.txt"), entry.Path);
        Assert.AreEqual("docs", entry.Parent!.Value.Name);
        Assert.AreEqual(100L, entry.Size);
    }

    [TestMethod]
    public async Task UnverifiedFreedRow_IsDetachedAndItsPathIsItsBareName()
    {
        await using var index = await OpenAsync(includeFreed: true);

        var entry = index.Search(new SearchQuery("orphan.txt", IncludeDeleted: true)).Single();

        Assert.IsTrue(entry.IsDeleted);
        Assert.AreEqual("orphan.txt", entry.Path);
        Assert.IsNull(entry.Parent);
    }

    [TestMethod]
    public async Task UnverifiedFreedRow_IsNobodysChildAndLookupNeverFindsIt()
    {
        await using var index = await OpenAsync(includeFreed: true);
        var docs = index.Find(Path.Combine(_directories.TreeRoot, "docs"))!.Value;
        var root = index.Root('T');

        CollectionAssert.AreEqual(new[] { "keep.txt" }, docs.Children().Select(child => child.Name).ToArray());
        CollectionAssert.DoesNotContain(root.Children().Select(child => child.Name).ToArray(), "orphan.txt");
        Assert.IsNull(index.Find(Path.Combine(_directories.TreeRoot, "docs", "orphan.txt")));
        Assert.IsNull(index.Find(Path.Combine(_directories.TreeRoot, "orphan.txt")));
        Assert.IsNull(index.Find(Path.Combine(_directories.TreeRoot, "docs", "trusted.txt")));
    }

    [TestMethod]
    public async Task UnverifiedFreedRow_IsUnderNoDirectoryButAVerifiedOneIsUnderItsAncestors()
    {
        await using var index = await OpenAsync(includeFreed: true);
        var docs = index.Find(Path.Combine(_directories.TreeRoot, "docs"))!.Value;
        var root = index.Root('T');

        string[] Names(FileEntry? under) => index
            .Search(new SearchQuery(".txt", Under: under, IncludeDeleted: true)).Select(entry => entry.Name)
            .Order().ToArray();

        CollectionAssert.AreEqual(new[] { "keep.txt", "trusted.txt" }, Names(docs));
        CollectionAssert.AreEqual(new[] { "dup.txt", "dup.txt", "keep.txt", "trusted.txt" }, Names(root));
        CollectionAssert.AreEqual(new[] { "dup.txt", "dup.txt", "keep.txt", "orphan.txt", "trusted.txt" },
            Names(null));
    }

    [TestMethod]
    public async Task Search_ReturnsFreedRowsOnlyWhenAskedAndEnumerateAgrees()
    {
        await using var index = await OpenAsync(includeFreed: true);

        string[] Search(bool includeDeleted) => index.Search(new SearchQuery(".txt", IncludeDeleted: includeDeleted))
            .Select(entry => entry.Name).Order().ToArray();
        string[] Enumerate(bool includeDeleted) => index
            .Enumerate(new SearchQuery(".txt", IncludeDeleted: includeDeleted)).Select(entry => entry.Name)
            .Order().ToArray();

        CollectionAssert.AreEqual(new[] { "dup.txt", "keep.txt" }, Search(includeDeleted: false));
        CollectionAssert.AreEqual(new[] { "dup.txt", "dup.txt", "keep.txt", "orphan.txt", "trusted.txt" },
            Search(includeDeleted: true));
        CollectionAssert.AreEqual(Search(false), Enumerate(false));
        CollectionAssert.AreEqual(Search(true), Enumerate(true));
    }

    [TestMethod]
    public async Task IncludeDeleted_StillAppliesEveryOtherFilter()
    {
        await using var index = await OpenAsync(includeFreed: true);

        var largeDeleted = index.Search(new SearchQuery(null, MinimumSize: 150, IncludeDeleted: true));
        var deletedDirectories = index.Search(new SearchQuery(null, Directories: true, IncludeDeleted: true));

        CollectionAssert.AreEqual(new[] { "orphan.txt" }, largeDeleted.Select(entry => entry.Name).ToArray());
        var goneDirectory = deletedDirectories.Single(entry => entry.IsDeleted);
        Assert.AreEqual("gone-dir", goneDirectory.Name);
        Assert.IsTrue(goneDirectory.IsDirectory);
    }

    [TestMethod]
    public async Task LargestAndDuplicateNames_StayLiveOnly()
    {
        await using var index = await OpenAsync(includeFreed: true);

        var largest = index.Largest(10).Select(entry => entry.Name).ToArray();

        CollectionAssert.DoesNotContain(largest, "orphan.txt");
        CollectionAssert.DoesNotContain(largest, "trusted.txt");
        Assert.AreEqual(0, index.DuplicateNames().Count, "One live dup.txt and one freed dup.txt are not duplicates.");
    }

    [TestMethod]
    public async Task WithoutTheOption_TheScanYieldsNoFreedRowsAndNothingIsDeleted()
    {
        await using var index = await OpenAsync(includeFreed: false);

        Assert.AreEqual(0, index.Search(new SearchQuery(null, IncludeDeleted: true))
            .Count(entry => entry.IsDeleted));
        Assert.AreEqual(index.Search(new SearchQuery(null)).Count,
            index.Search(new SearchQuery(null, IncludeDeleted: true)).Count);
    }

    // The scripted source behaves like the live one: freed records are yielded only when the scan asks for them.
    static IEnumerable<IReadOnlyList<MftRecord>> Scan(MftRecordScanOptions options)
    {
        yield return
        [
            Record(5, 5, ".", 5, 5, directory: true),
            Record(6, 5, "docs", 6, 5, directory: true),
            Record(7, 6, "keep.txt", 3, 6, size: 10),
            Record(10, 5, "dup.txt", 3, 5, size: 5)
        ];
        if (options.IncludeFreed)
        {
            yield return
            [
                Record(8, 6, "trusted.txt", 9, 6, size: 100, freed: true),
                Record(9, 6, "orphan.txt", 9, 77, size: 200, freed: true),
                Record(11, 5, "dup.txt", 4, 5, size: 5, freed: true),
                Record(12, 5, "gone-dir", 4, 5, directory: true, freed: true)
            ];
        }
    }

    static MftRecord Record(ulong recordNumber, ulong parent, string name, ushort sequence, ushort parentSequence,
        bool directory = false, long size = 0, bool freed = false)
    {
        var flags = (ushort)((freed ? 0 : 1) | (directory ? 2 : 0));
        return new MftRecord(recordNumber, parent, new MftRecordFields(flags,
            directory ? FileAttributes.Directory : FileAttributes.Normal, size, FixedMoment.ToFileTimeUtc(), sequence,
            parentSequence), name, null);
    }

    Task<FileIndex> OpenAsync(bool includeFreed)
    {
        var seams = new LocalMftBlockProducer.Seams(_ => Volume,
            (_, _, _, _, scanOptions, _) => Scan(scanOptions), () => FixedMoment);
        var producer = new LocalMftBlockProducer(new BrokerScanOptions { IncludeFreed = includeFreed }, seams);
        return FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _directories.TreeRoot, 0x0BADF00D)],
            CacheDirectory = _directories.CacheDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = producer.CreateIndexSource()
        }, CancellationToken.None);
    }
}
