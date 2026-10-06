using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>The three ways a search pattern is compared with a name, over names that hold wildcard characters.</summary>
[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class SearchQueryMatchModeTests
{
    static readonly DateTime Moment = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder(slotCapacity: 512, namePoolCapacity: 8192);
        var root = _builder.AddRoot();
        foreach (var name in new[] { "report.pdf", "Report.docx", "a*b.txt", "axxb.txt", "a?c.txt", "abc.txt", "a*b" })
        {
            _builder.AddRow(name, root, RowFlags.InUse, 10, Moment, sequenceNumber: 0);
        }

        _builder.Complete(Moment);
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, _builder.OpenForReading(out _)!)]);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _snapshot.ReleaseNowAsync();
        _builder.Dispose();
    }

    string[] Names(string? pattern, NameMatchMode mode, bool caseSensitive = false)
    {
        var results = SearchEngine.Search(_snapshot, new SearchQuery(pattern, mode, caseSensitive));
        var streamed = SearchEngine.Enumerate(_snapshot, new SearchQuery(pattern, mode, caseSensitive)).ToList();
        CollectionAssert.AreEqual(results.Select(entry => entry.Name).ToArray(),
            streamed.Select(entry => entry.Name).ToArray(), "Search and Enumerate must share one predicate.");
        return results.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    [TestMethod]
    public void MatchMode_DefaultsToSubstring()
    {
        Assert.AreEqual(NameMatchMode.Substring, new SearchQuery("x").MatchMode);
    }

    [TestMethod]
    public void Exact_ComparesTheWholeBasename()
    {
        CollectionAssert.AreEqual(new[] { "report.pdf" }, Names("report.pdf", NameMatchMode.Exact));
        CollectionAssert.AreEqual(new[] { "report.pdf" }, Names("REPORT.PDF", NameMatchMode.Exact));
        Assert.AreEqual(0, Names("REPORT.PDF", NameMatchMode.Exact, caseSensitive: true).Length);
        Assert.AreEqual(0, Names("report", NameMatchMode.Exact).Length);
    }

    [TestMethod]
    public void Exact_TreatsWildcardsAsOrdinaryCharacters()
    {
        CollectionAssert.AreEqual(new[] { "a*b.txt" }, Names("a*b.txt", NameMatchMode.Exact));
        CollectionAssert.AreEqual(new[] { "a?c.txt" }, Names("a?c.txt", NameMatchMode.Exact));
    }

    [TestMethod]
    public void Substring_TreatsWildcardsAsOrdinaryCharacters()
    {
        CollectionAssert.AreEqual(new[] { "a*b", "a*b.txt" }, Names("a*b", NameMatchMode.Substring));
        CollectionAssert.AreEqual(new[] { "a?c.txt" }, Names("a?c", NameMatchMode.Substring));
        Assert.AreEqual(0, Names("*.pdf", NameMatchMode.Substring).Length);
    }

    [TestMethod]
    public void Glob_InterpretsWildcardsAgainstTheWholeName()
    {
        CollectionAssert.AreEqual(new[] { "report.pdf" }, Names("*.pdf", NameMatchMode.Glob));
        CollectionAssert.AreEqual(new[] { "a*b.txt", "a?c.txt", "abc.txt", "axxb.txt" },
            Names("a*.txt", NameMatchMode.Glob));
        CollectionAssert.AreEqual(new[] { "a?c.txt", "abc.txt" }, Names("a?c*", NameMatchMode.Glob));
        CollectionAssert.AreEqual(new[] { "a*b", "a*b.txt", "a?c.txt", "abc.txt", "axxb.txt" },
            Names("a?*", NameMatchMode.Glob));
        Assert.AreEqual(0, Names("report", NameMatchMode.Glob).Length, "a glob matches the whole name, not a part of it");
    }

    [TestMethod]
    public void NullPattern_DisablesNameFilteringInEveryMode()
    {
        var expected = Names(null, NameMatchMode.Substring);

        Assert.AreEqual(8, expected.Length, "the seven files and the root directory");
        CollectionAssert.AreEqual(expected, Names(null, NameMatchMode.Exact));
        CollectionAssert.AreEqual(expected, Names(null, NameMatchMode.Glob));
    }

    [TestMethod]
    public void EmptyPattern_MatchesEveryNameWhenSubstringAndOnlyAnEmptyNameOtherwise()
    {
        Assert.AreEqual(8, Names(string.Empty, NameMatchMode.Substring).Length);
        CollectionAssert.AreEqual(new[] { string.Empty }, Names(string.Empty, NameMatchMode.Exact));
        CollectionAssert.AreEqual(new[] { string.Empty }, Names(string.Empty, NameMatchMode.Glob));
    }

    [TestMethod]
    public void UndefinedMode_ThrowsBeforeAnyRowIsRead()
    {
        var query = new SearchQuery(null, (NameMatchMode)99);

        var searchFailure = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => SearchEngine.Search(_snapshot, query));

        Assert.AreEqual("query", searchFailure.ParamName);
        StringAssert.StartsWith(searchFailure.Message, "Unknown name match mode.");
    }
}
