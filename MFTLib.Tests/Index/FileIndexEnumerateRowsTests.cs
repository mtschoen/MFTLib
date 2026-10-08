using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexEnumerateRowsTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    OwnedIndexDirectories _directories = null!;
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(_treeRoot);
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    string FirstRoot => Path.Combine(_treeRoot, "first");

    string SecondRoot => Path.Combine(_treeRoot, "second");

    static List<FileEntry> Collect(FileIndex index, SearchQuery query, CancellationToken cancellationToken = default)
    {
        var entries = new List<FileEntry>();
        foreach (var row in index.EnumerateRows(query, cancellationToken))
        {
            entries.Add(row.ToEntry());
        }

        return entries;
    }

    static void AssertRowsMatchSearch(FileIndex index, SearchQuery query)
    {
        var expected = index.Search(query).Select(entry => entry.RecordKey).OrderBy(key => key.ToString()).ToArray();
        var actual = Collect(index, query).Select(entry => entry.RecordKey).OrderBy(key => key.ToString()).ToArray();
        CollectionAssert.AreEqual(expected, actual, $"EnumerateRows and Search disagree for {query}.");
    }

    [TestMethod]
    public async Task EnumerateRows_MatchesSearchAcrossAMultiDriveIndex()
    {
        await using var index = await OpenTwoDriveIndexAsync();

        AssertRowsMatchSearch(index, new SearchQuery(null));
        AssertRowsMatchSearch(index, new SearchQuery(null, Directories: false));
        AssertRowsMatchSearch(index, new SearchQuery(null, Directories: true));
        AssertRowsMatchSearch(index, new SearchQuery("*.md", NameMatchMode.Glob));
        AssertRowsMatchSearch(index, new SearchQuery("readme"));
        AssertRowsMatchSearch(index, new SearchQuery("readme.md", NameMatchMode.Exact));
        AssertRowsMatchSearch(index, new SearchQuery(null, MinimumSize: 100));
        AssertRowsMatchSearch(index, new SearchQuery(null, IncludeDeleted: true));

        var documents = index.Find(Path.Combine(FirstRoot, "Documents"))!.Value;
        AssertRowsMatchSearch(index, new SearchQuery("readme", Under: documents));
        AssertRowsMatchSearch(index, new SearchQuery(null, Under: documents));
        AssertRowsMatchSearch(index, new SearchQuery(null, Under: default(FileEntry)));
    }

    [TestMethod]
    public async Task EnumerateRows_RowViewAgreesWithItsEntry()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        AssertRowViews(index);
    }

    static void AssertRowViews(FileIndex index)
    {
        var seen = 0;
        foreach (var row in index.EnumerateRows(new SearchQuery(null)))
        {
            var entry = row.ToEntry();
            Assert.AreEqual(entry.Name, row.Name.ToString());
            Assert.AreEqual(entry.RecordKey.DriveLetter, row.DriveLetter);
            Assert.AreEqual(entry.Size, row.Size);
            Assert.AreEqual(entry.IsSizeKnown, row.IsSizeKnown);
            Assert.AreEqual(entry.IsDirectory, row.IsDirectory);
            Assert.AreEqual(entry.IsDeleted, row.IsDeleted);
            seen++;
        }

        Assert.IsTrue(seen > 0, "The two-drive fixture has rows.");
    }

    [TestMethod]
    public async Task EnumerateRows_UndefinedMatchMode_ThrowsAtCallTime()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        var query = new SearchQuery("readme", (NameMatchMode)99);
        AssertInvalidMatchMode(index, query);
    }

    static void AssertInvalidMatchMode(FileIndex index, SearchQuery query)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => { index.EnumerateRows(query); });
    }

    [TestMethod]
    public async Task EnumerateRows_NullQuery_ThrowsAtCallTime()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        AssertNullQuery(index);
    }

    static void AssertNullQuery(FileIndex index)
    {
        Assert.ThrowsException<ArgumentNullException>(() => { index.EnumerateRows(null!); });
    }

    [TestMethod]
    public async Task EnumerateRows_WithACancelledToken_ThrowsBeforeTheFirstRow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        AssertCancelledBeforeFirstRow(index, source.Token);
    }

    static void AssertCancelledBeforeFirstRow(FileIndex index, CancellationToken cancellationToken)
    {
        Assert.ThrowsException<OperationCanceledException>(() => Collect(index, new SearchQuery(null), cancellationToken));
    }

    [TestMethod]
    public async Task EnumerateRows_OnADisposedIndex_Throws()
    {
        var index = await OpenTwoDriveIndexAsync();
        await index.DisposeAsync();
        Assert.ThrowsException<ObjectDisposedException>(() => Collect(index, new SearchQuery(null)));
    }

    [TestMethod]
    public async Task EnumerateRows_CompletedToTheEnd_LeavesNoBorrowOutstanding()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        Assert.IsTrue(Collect(index, new SearchQuery(null)).Count > 0);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    [TestMethod]
    public async Task EnumerateRows_BreakOnFirstRow_ReturnsItsBorrow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        BreakOnFirstRow(index);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    static void BreakOnFirstRow(FileIndex index)
    {
        var seen = false;
        foreach (var row in index.EnumerateRows(new SearchQuery(null)))
        {
            _ = row.Size;
            seen = true;
            Assert.AreEqual(1, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
            break;
        }

        Assert.IsTrue(seen);
    }

    [TestMethod]
    public async Task EnumerateRows_LoopBodyThrows_ReturnsItsBorrow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        AssertLoopBodyThrows(index);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    static void AssertLoopBodyThrows(FileIndex index)
    {
        Assert.ThrowsException<InvalidOperationException>(() => ThrowFromLoopBody(index));
    }

    static void ThrowFromLoopBody(FileIndex index)
    {
        foreach (var row in index.EnumerateRows(new SearchQuery(null)))
        {
            _ = row.Size;
            Assert.AreEqual(1, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
            throw new InvalidOperationException("Loop body failure.");
        }
    }

    [TestMethod]
    public async Task EnumerateRows_CancelledMidScan_ReturnsItsBorrow()
    {
        await using var index = await OpenSyntheticIndexAsync(10_000);
        using var source = new CancellationTokenSource();
        CancelInsideLoop(index, source);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    static void CancelInsideLoop(FileIndex index, CancellationTokenSource source)
    {
        var seen = 0;
        try
        {
            foreach (var row in index.EnumerateRows(new SearchQuery(null), source.Token))
            {
                _ = row.Size;
                seen++;
                if (seen == 1)
                {
                    Assert.AreEqual(1, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
                    source.Cancel();
                }
            }

            Assert.Fail("Cancellation must be observed while rows remain.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(seen > 0 && seen <= RowScanner.CancellationCheckIntervalRows,
                "Cancellation must be observed no later than 4096 rows after the first row.");
        }
    }

    [TestMethod]
    public async Task EnumerateRows_WithoutIteration_TakesNoBorrow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        CreateWithoutIteration(index);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    static void CreateWithoutIteration(FileIndex index)
    {
        index.EnumerateRows(new SearchQuery(null));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task EnumerateRows_AccessAfterSharedBorrowReturned_Throws(bool throughCopy, bool readCurrent)
    {
        await using var index = await OpenTwoDriveIndexAsync();
        AssertDisposedAccess(index, throughCopy, readCurrent);
    }

    static void AssertDisposedAccess(FileIndex index, bool throughCopy, bool readCurrent)
    {
        using var enumerator = index.EnumerateRows(new SearchQuery(null)).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        using var copy = enumerator;
        enumerator.Dispose();
        try
        {
            if (readCurrent)
            {
                _ = throughCopy ? copy.Current : enumerator.Current;
            }
            else
            {
                _ = throughCopy ? copy.MoveNext() : enumerator.MoveNext();
            }

            Assert.Fail("A returned shared borrow must reject access before reading mapped memory.");
        }
        catch (ObjectDisposedException exception)
        {
            Assert.AreEqual(nameof(IndexRowEnumerator), exception.ObjectName);
        }
    }

    [TestMethod]
    public async Task EnumerateRows_RepeatedAndCopiedDispose_ReturnsExactlyOneBorrow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        AssertRepeatedDispose(index);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    static void AssertRepeatedDispose(FileIndex index)
    {
        using var source = new CancellationTokenSource();
        using var enumerator = index.EnumerateRows(new SearchQuery(null), source.Token).GetEnumerator();
        using var copy = enumerator;
        Assert.AreEqual(1, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        copy.Dispose();
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        enumerator.Dispose();
        enumerator.Dispose();
        copy.Dispose();
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);

        using var later = index.EnumerateRows(new SearchQuery(null)).GetEnumerator();
        Assert.AreEqual(1, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        Assert.IsTrue(later.MoveNext());
    }

    async Task<FileIndex> OpenTwoDriveIndexAsync()
    {
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Documents"));
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Pictures"));
        Directory.CreateDirectory(Path.Combine(SecondRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "readme.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "report.pdf"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Pictures", "holiday.jpg"), new string('y', 5000));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "notes.md"), new string('z', 300));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "Pictures", "readme.md"), "world");

        return await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', FirstRoot, 1), new IndexedDrive('U', SecondRoot, 2)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
    }

    Task<FileIndex> OpenSyntheticIndexAsync(uint rowCount)
    {
        return FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, TestVolumeSerial.GetNext())],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(SeededBlocks.Build(request, rowCount, i => $"file{i}.dat", FixedMoment), JournalId: 7,
                    NextUsn: 4096, SkippedRecordCount: 0)))
        }, CancellationToken.None);
    }
}
