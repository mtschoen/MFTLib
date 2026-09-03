using System.Diagnostics.CodeAnalysis;
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
        _documentsRow = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Older);
        _picturesRow = _builder.AddRow("Pictures", root, RowFlags.InUse | RowFlags.Directory, 0, Older);
        _builder.AddRow("report.pdf", _documentsRow, RowFlags.InUse, 4096, Newer);
        _builder.AddRow("Report.docx", _documentsRow, RowFlags.InUse, 100, Older);
        _builder.AddRow("holiday.jpg", _picturesRow, RowFlags.InUse, 2_000_000, Newer);
        _builder.AddRow("deleted.pdf", _documentsRow, RowFlags.InUse | RowFlags.Tombstone, 10, Older);
        _builder.Complete(Newer);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block, deleteFileOnRelease: false)]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
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
    public void Search_SizeFilter_ExcludesSizeUnknownRows()
    {
        using var builder = new SyntheticBlockBuilder('S');
        var root = builder.AddRoot();
        builder.AddRow("known-zero.txt", root, RowFlags.InUse, 0, Newer);
        builder.AddRow("unknown-size.txt", root, RowFlags.InUse | RowFlags.SizeUnknown, 0, Newer);
        builder.AddRow("known-large.txt", root, RowFlags.InUse, 1000, Newer);
        builder.Complete(Newer);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('S', 0, block, deleteFileOnRelease: false)]);
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
            snapshot.ReleaseNow();
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
    public void Search_OverALargeDriveUsesEveryPartitionAndFindsEveryMatch()
    {
        using var builder = new SyntheticBlockBuilder('Y', slotCapacity: 300_000, namePoolCapacity: 8_000_000);
        var root = builder.AddRoot();
        const int fileCount = 200_000;
        for (var index = 0; index < fileCount; index++)
        {
            builder.AddRow($"file{index}.dat", root, RowFlags.InUse, index, Older);
        }

        builder.Complete(Newer);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('Y', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var results = SearchEngineTestAccess.Search(snapshot, new SearchQuery("*.dat"));
            Assert.AreEqual(fileCount, results.Count);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
}

static class SearchEngineTestAccess
{
    public static List<FileEntry> Search(Snapshot snapshot, SearchQuery query)
    {
        return SearchEngine.Search(snapshot, query);
    }
}
