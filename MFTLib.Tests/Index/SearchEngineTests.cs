using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class SearchEngineTests
{
    static readonly DateTime Older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime Newer = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;
    uint _documentsRow;
    uint _picturesRow;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder(slotCapacity: 512, namePoolCapacity: 8192);
        var root = _builder.AddRoot();
        _documentsRow = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Older, sequenceNumber: 0);
        _picturesRow = _builder.AddRow("Pictures", root, RowFlags.InUse | RowFlags.Directory, 0, Older, sequenceNumber: 0);
        _builder.AddRow("report.pdf", _documentsRow, RowFlags.InUse, 4096, Newer, sequenceNumber: 0);
        _builder.AddRow("Report.docx", _documentsRow, RowFlags.InUse, 100, Older, sequenceNumber: 0);
        _builder.AddRow("holiday.jpg", _picturesRow, RowFlags.InUse, 2_000_000, Newer, sequenceNumber: 0);
        _builder.AddRow("deleted.pdf", _documentsRow, RowFlags.InUse | RowFlags.Tombstone, 10, Older, sequenceNumber: 0);
        _builder.Complete(Newer);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block)]);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _snapshot.ReleaseNowAsync();
        _builder.Dispose();
    }

    static string[] NamesOf(IReadOnlyList<FileEntry> entries)
    {
        return entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    [TestMethod]
    public void Search_SubstringIsCaseInsensitiveByDefault()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery("report"));
        CollectionAssert.AreEqual(new[] { "Report.docx", "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public void Search_CaseSensitiveNarrowsTheResult()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery("report", CaseSensitive: true));
        CollectionAssert.AreEqual(new[] { "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public void Search_GlobPatternMatchesTheWholeName()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery("*.pdf"));
        CollectionAssert.AreEqual(new[] { "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public void Search_NullPatternMatchesEveryLiveRow()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null));
        // Root, two directories, and three live files. The tombstoned row is excluded.
        Assert.AreEqual(6, results.Count);
    }

    [TestMethod]
    public void Search_ExcludesTombstonedRows()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery("deleted"));
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Search_DirectoriesTrueReturnsOnlyDirectories()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null, Directories: true));
        Assert.IsTrue(results.All(entry => entry.IsDirectory));
    }

    [TestMethod]
    public void Search_DirectoriesFalseReturnsOnlyFiles()
    {
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null, Directories: false));
        CollectionAssert.AreEqual(new[] { "Report.docx", "holiday.jpg", "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public void Search_SizeBoundsAreInclusive()
    {
        var results = SearchEngineTestAccess.Search(_snapshot,
            new SearchQuery(null, Directories: false, MinimumSize: 100, MaximumSize: 4096));
        CollectionAssert.AreEqual(new[] { "Report.docx", "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public async Task Search_SizeFilter_ExcludesSizeUnknownRows()
    {
        using var builder = new SyntheticBlockBuilder('S');
        var root = builder.AddRoot();
        builder.AddRow("known-zero.txt", root, RowFlags.InUse, 0, Newer, sequenceNumber: 0);
        builder.AddRow("unknown-size.txt", root, RowFlags.InUse | RowFlags.SizeUnknown, 0, Newer, sequenceNumber: 0);
        builder.AddRow("known-large.txt", root, RowFlags.InUse, 1000, Newer, sequenceNumber: 0);
        builder.Complete(Newer);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('S', 0, block)]);
        try
        {
            var resultsMin = SearchEngineTestAccess.Search(snapshot, new SearchQuery(null, MinimumSize: 0));
            Assert.IsTrue(resultsMin.Any(entry => entry.Name == "known-zero.txt"));
            Assert.IsFalse(resultsMin.Any(entry => entry.Name == "unknown-size.txt"));

            var resultsMax = SearchEngineTestAccess.Search(snapshot, new SearchQuery(null, MaximumSize: 100));
            Assert.IsTrue(resultsMax.Any(entry => entry.Name == "known-zero.txt"));
            Assert.IsFalse(resultsMax.Any(entry => entry.Name == "unknown-size.txt"));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Search_ModifiedBoundsAreInclusive()
    {
        var results = SearchEngineTestAccess.Search(_snapshot,
            new SearchQuery(null, Directories: false, ModifiedAfter: Newer));
        CollectionAssert.AreEqual(new[] { "holiday.jpg", "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public void Search_UnderRestrictsToTheSubtreeInclusive()
    {
        var documents = FileEntry.Create(_snapshot, 0, _documentsRow);
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null, Under: documents));
        CollectionAssert.AreEqual(new[] { "Documents", "Report.docx", "report.pdf" }, NamesOf(results));
    }

    [TestMethod]
    public void Search_UnderADifferentSubtreeExcludesSiblings()
    {
        var pictures = FileEntry.Create(_snapshot, 0, _picturesRow);
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery("report", Under: pictures));
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Search_Under_InvalidAncestor_ReturnsNoResults()
    {
        var invalidAncestor = default(FileEntry);
        var results = SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null, Under: invalidAncestor));
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Search_Under_InvalidAncestor_WithCancelledToken_ThrowsOperationCanceled()
    {
        var invalidAncestor = default(FileEntry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
            SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null, Under: invalidAncestor), token));
    }

    [TestMethod]
    public async Task Search_Under_WhenCandidateExceedsTheDepthCap_ThrowsInvalidDataException()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 512, namePoolCapacity: 16384);
        var root = builder.AddRoot();
        var parent = root;
        for (var level = 0; level < BlockLayout.MaximumPathDepth; level++)
        {
            parent = builder.AddRow($"d{level}", parent, RowFlags.InUse | RowFlags.Directory, 0, Older, sequenceNumber: 0);
        }

        builder.AddRow("needle.txt", parent, RowFlags.InUse, 1, Newer, sequenceNumber: 0);
        builder.Complete(Newer);
        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('Z', 0, block)]);
        try
        {
            var ancestor = FileEntry.Create(snapshot, 0, root);
            var exception = Assert.ThrowsException<InvalidDataException>(() =>
                SearchEngineTestAccess.Search(snapshot, new SearchQuery("needle.txt", Under: ancestor)));
            StringAssert.Contains(exception.Message, BlockLayout.MaximumPathDepth.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Search_Under_WhenCandidateHasACyclicParentColumn_ExcludesCyclicCandidateWithoutThrowing()
    {
        using var builder = new SyntheticBlockBuilder('Z');
        var root = builder.AddRoot();
        var dirA = builder.AddRow("dirA", 2, RowFlags.InUse | RowFlags.Directory, 0, Older, sequenceNumber: 0);
        var dirB = builder.AddRow("dirB", dirA, RowFlags.InUse | RowFlags.Directory, 0, Older, sequenceNumber: 0);
        builder.AddRow("cyclic.txt", dirB, RowFlags.InUse, 10, Newer, sequenceNumber: 0);

        var validDir = builder.AddRow("validDir", root, RowFlags.InUse | RowFlags.Directory, 0, Older, sequenceNumber: 0);
        builder.AddRow("valid.txt", validDir, RowFlags.InUse, 20, Newer, sequenceNumber: 0);
        builder.Complete(Newer);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('Z', 0, block)]);
        try
        {
            var ancestor = FileEntry.Create(snapshot, 0, root);
            var results = SearchEngineTestAccess.Search(snapshot, new SearchQuery("*.txt", Under: ancestor));
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("valid.txt", results[0].Name);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Search_OverALargeDriveUsesEveryPartitionAndFindsEveryMatch()
    {
        using var builder = new SyntheticBlockBuilder('Y', slotCapacity: 300_000, namePoolCapacity: 8_000_000);
        var root = builder.AddRoot();
        const int fileCount = 200_000;
        for (var index = 0; index < fileCount; index++)
        {
            builder.AddRow($"file{index}.dat", root, RowFlags.InUse, index, Older, sequenceNumber: 0);
        }

        builder.Complete(Newer);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('Y', 0, block)]);
        try
        {
            var results = SearchEngineTestAccess.Search(snapshot, new SearchQuery("*.dat"));
            Assert.AreEqual(fileCount, results.Count);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     The parallel path reports cancellation as <see cref="OperationCanceledException" />,
    ///     not as an <see cref="AggregateException" /> a caller would have to unwrap: the loop
    ///     carries the query's token, so the partitions that observe it all report the same
    ///     cancellation.
    /// </summary>
    [TestMethod]
    public async Task Search_OverALargeDriveWithACancelledToken_ThrowsOperationCanceled()
    {
        const int fileCount = (int)ScanPartitioning.SingleThreadedRowThreshold + 1024;
        using var builder = new SyntheticBlockBuilder('Y', slotCapacity: (uint)fileCount + 8,
            namePoolCapacity: (uint)fileCount * 32);
        var root = builder.AddRoot();
        for (var index = 0; index < fileCount; index++)
        {
            builder.AddRow($"file{index}.dat", root, RowFlags.InUse, index, Older, sequenceNumber: 0);
        }

        builder.Complete(Newer);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('Y', 0, block)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        try
        {
            Assert.ThrowsException<OperationCanceledException>(
                () => SearchEngineTestAccess.Search(snapshot, new SearchQuery("*.dat"), token));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Search_Under_WithCancelledToken_ThrowsOperationCanceled()
    {
        var documents = FileEntry.Create(_snapshot, 0, _documentsRow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
            SearchEngineTestAccess.Search(_snapshot, new SearchQuery(null, Under: documents), token));
    }

    /// <summary>
    ///     A block whose producer wrote no rows at all partitions to no ranges, so the
    ///     search completes empty without starting the parallel machinery.
    /// </summary>
    [TestMethod]
    public async Task Search_OverAnEmptyDriveBlock_ReturnsNoResults()
    {
        using var builder = new SyntheticBlockBuilder('Y');
        var snapshot = Snapshot.Create([new DriveBlock('Y', 0, builder.OpenForWriting())]);
        try
        {
            Assert.AreEqual(0u, builder.OpenForWriting().Header.RowCount);
            Assert.AreEqual(0, SearchEngineTestAccess.Search(snapshot, new SearchQuery(null)).Count);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}

static class SearchEngineTestAccess
{
    public static List<FileEntry> Search(Snapshot snapshot, SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        return SearchEngine.Search(snapshot, query, cancellationToken);
    }
}
