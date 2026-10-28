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
    RootBlock _rootBlock = null!;
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
        _rootBlock = new RootBlock(new VolumeKey('T'), block);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _rootBlock.ReleaseNowAsync();
        _builder.Dispose();
    }

    static string[] NamesOf(IReadOnlyList<FileEntry> entries)
    {
        return [.. entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal)];
    }

    [TestMethod]
    public void Search_SubstringIsCaseInsensitiveByDefault()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery("report"), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(["Report.docx", "report.pdf"], NamesOf(results));
    }

    [TestMethod]
    public void Search_CaseSensitiveNarrowsTheResult()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery("report", CaseSensitive: true), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(["report.pdf"], NamesOf(results));
    }

    [TestMethod]
    public void Search_GlobPatternMatchesTheWholeName()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery("*.pdf", NameMatchMode.Glob), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(["report.pdf"], NamesOf(results));
    }

    [TestMethod]
    public void Search_NullPatternMatchesEveryLiveRow()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery(null), cancellationToken: TestContext.CancellationToken);
        // Root, two directories, and three live files. The tombstoned row is excluded.
        Assert.HasCount(6, results);
    }

    [TestMethod]
    public void Search_ExcludesTombstonedRows()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery("deleted"), cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void Search_DirectoriesTrueReturnsOnlyDirectories()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery(null, IsDirectory: true), cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(results.All(entry => entry.IsDirectory));
    }

    [TestMethod]
    public void Search_DirectoriesFalseReturnsOnlyFiles()
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery(null, IsDirectory: false), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(["Report.docx", "holiday.jpg", "report.pdf"], NamesOf(results));
    }

    [TestMethod]
    public void Search_SizeBoundsAreInclusive()
    {
        var results = SearchEngine.Search(_rootBlock,
            new SearchQuery(null, IsDirectory: false, SizeMinimum: 100, SizeMaximum: 4096), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(["Report.docx", "report.pdf"], NamesOf(results));
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
        var rootBlock = new RootBlock(new VolumeKey('S'), block);
        try
        {
            var resultsMin = SearchEngine.Search(rootBlock, new SearchQuery(null, SizeMinimum: 0), cancellationToken: TestContext.CancellationToken);
            Assert.Contains(entry => entry.Name == "known-zero.txt", resultsMin);
            Assert.DoesNotContain(entry => entry.Name == "unknown-size.txt", resultsMin);

            var resultsMax = SearchEngine.Search(rootBlock, new SearchQuery(null, SizeMaximum: 100), cancellationToken: TestContext.CancellationToken);
            Assert.Contains(entry => entry.Name == "known-zero.txt", resultsMax);
            Assert.DoesNotContain(entry => entry.Name == "unknown-size.txt", resultsMax);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    [DataRow(true, new[] { "holiday.jpg", "report.pdf" })]
    [DataRow(false, new[] { "Report.docx" })]
    public void Search_LastWriteTimeBoundsAreInclusive(bool isLowerBound, string[] expectedNames)
    {
        var results = SearchEngine.Search(_rootBlock,
            new SearchQuery(null, IsDirectory: false,
                LastWriteTimeMinimum: isLowerBound ? Newer : null,
                LastWriteTimeMaximum: isLowerBound ? null : Older), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(expectedNames, NamesOf(results));
    }

    [TestMethod]
    public void Search_SubtreeRestrictsToTheEntryAndBelow()
    {
        var documents = FileEntry.Create(_rootBlock, _documentsRow);
        var results = SearchEngine.Search(_rootBlock, new SearchQuery(null, Subtree: documents), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(["Documents", "Report.docx", "report.pdf"], NamesOf(results));
    }

    [TestMethod]
    public void Search_UnderADifferentSubtreeExcludesSiblings()
    {
        var pictures = FileEntry.Create(_rootBlock, _picturesRow);
        var results = SearchEngine.Search(_rootBlock, new SearchQuery("report", Subtree: pictures), cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void Search_Under_DefaultAncestor_ThrowsArgumentException()
    {
        var invalidAncestor = default(FileEntry);
        Assert.ThrowsExactly<ArgumentException>(
            () => SearchEngine.Search(_rootBlock, new SearchQuery(null, Subtree: invalidAncestor), cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public void Search_Under_InvalidAncestor_WithCancelledToken_ThrowsOperationCanceled()
    {
        var invalidAncestor = default(FileEntry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SearchEngine.Search(_rootBlock, new SearchQuery(null, Subtree: invalidAncestor), cancellationToken: token));
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
        var rootBlock = new RootBlock(new VolumeKey('Z'), block);
        try
        {
            var ancestor = FileEntry.Create(rootBlock, root);
            var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                SearchEngine.Search(rootBlock, new SearchQuery("needle.txt", Subtree: ancestor), cancellationToken: TestContext.CancellationToken));
            Assert.Contains(BlockLayout.MaximumPathDepth.ToString(CultureInfo.InvariantCulture), exception.Message);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
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
        var rootBlock = new RootBlock(new VolumeKey('Z'), block);
        try
        {
            var ancestor = FileEntry.Create(rootBlock, root);
            var results = SearchEngine.Search(rootBlock, new SearchQuery("*.txt", NameMatchMode.Glob, Subtree: ancestor), cancellationToken: TestContext.CancellationToken);
            Assert.HasCount(1, results);
            Assert.AreEqual("valid.txt", results[0].Name);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Search_OverALargeRootUsesEveryPartitionAndFindsEveryMatch()
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
        var rootBlock = new RootBlock(new VolumeKey('Y'), block);
        try
        {
            var results = SearchEngine.Search(rootBlock, new SearchQuery("*.dat", NameMatchMode.Glob), cancellationToken: TestContext.CancellationToken);
            Assert.HasCount(fileCount, results);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Search_WithACancelledToken_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => SearchEngine.Search(_rootBlock, new SearchQuery("*.dat", NameMatchMode.Glob), cancellationToken: token));
    }

    [TestMethod]
    public void Search_Under_WithCancelledToken_ThrowsOperationCanceled()
    {
        var documents = FileEntry.Create(_rootBlock, _documentsRow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SearchEngine.Search(_rootBlock, new SearchQuery(null, Subtree: documents), cancellationToken: token));
    }

    [TestMethod]
    public async Task Search_OverAnEmptyRootBlock_ReturnsNoResults()
    {
        using var builder = new SyntheticBlockBuilder('Y');
        var rootBlock = new RootBlock(new VolumeKey('Y'), builder.OpenForWriting());
        try
        {
            Assert.AreEqual(0u, builder.OpenForWriting().Header.RowCount);
            Assert.IsEmpty(SearchEngine.Search(rootBlock, new SearchQuery(null), cancellationToken: TestContext.CancellationToken));
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
