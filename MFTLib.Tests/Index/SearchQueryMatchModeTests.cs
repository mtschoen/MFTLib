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
    RootBlock _rootBlock = null!;

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
        _rootBlock = new RootBlock(new VolumeKey('T'), _builder.OpenForReading(out _)!);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _rootBlock.ReleaseNowAsync();
        _builder.Dispose();
    }

    string[] Names(string? pattern, NameMatchMode mode, bool caseSensitive = false)
    {
        var results = SearchEngine.Search(_rootBlock, new SearchQuery(pattern, mode, caseSensitive),
            TestContext.CancellationToken);
        var streamed = SearchEngine.Enumerate(_rootBlock, new SearchQuery(pattern, mode, caseSensitive),
            TestContext.CancellationToken).ToList();
        Assert.AreSequenceEqual([.. results.Select(entry => entry.Name)], [.. streamed.Select(entry => entry.Name)], "Search and Enumerate must share one predicate.");
        return [.. results.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal)];
    }

    [TestMethod]
    public void MatchMode_DefaultsToSubstring()
    {
        Assert.AreEqual(NameMatchMode.Substring, new SearchQuery("x").MatchMode);
    }

    [TestMethod]
    public void Exact_ComparesTheWholeBasename()
    {
        Assert.AreSequenceEqual(["report.pdf"], Names("report.pdf", NameMatchMode.Exact));
        Assert.AreSequenceEqual(["report.pdf"], Names("REPORT.PDF", NameMatchMode.Exact));
        Assert.IsEmpty(Names("REPORT.PDF", NameMatchMode.Exact, caseSensitive: true));
        Assert.IsEmpty(Names("report", NameMatchMode.Exact));
    }

    [TestMethod]
    public void Exact_TreatsWildcardsAsOrdinaryCharacters()
    {
        Assert.AreSequenceEqual(["a*b.txt"], Names("a*b.txt", NameMatchMode.Exact));
        Assert.AreSequenceEqual(["a?c.txt"], Names("a?c.txt", NameMatchMode.Exact));
    }

    [TestMethod]
    public void Substring_TreatsWildcardsAsOrdinaryCharacters()
    {
        Assert.AreSequenceEqual(["a*b", "a*b.txt"], Names("a*b", NameMatchMode.Substring));
        Assert.AreSequenceEqual(["a?c.txt"], Names("a?c", NameMatchMode.Substring));
        Assert.IsEmpty(Names("*.pdf", NameMatchMode.Substring));
    }

    [TestMethod]
    public void Glob_InterpretsWildcardsAgainstTheWholeName()
    {
        Assert.AreSequenceEqual(["report.pdf"], Names("*.pdf", NameMatchMode.Glob));
        Assert.AreSequenceEqual(["a*b.txt", "a?c.txt", "abc.txt", "axxb.txt"], Names("a*.txt", NameMatchMode.Glob));
        Assert.AreSequenceEqual(["a?c.txt", "abc.txt"], Names("a?c*", NameMatchMode.Glob));
        Assert.AreSequenceEqual(["a*b", "a*b.txt", "a?c.txt", "abc.txt", "axxb.txt"], Names("a?*", NameMatchMode.Glob));
        Assert.IsEmpty(Names("report", NameMatchMode.Glob), "a glob matches the whole name, not a part of it");
    }

    [TestMethod]
    public void NullPattern_DisablesNameFilteringInEveryMode()
    {
        var expected = Names(null, NameMatchMode.Substring);

        Assert.HasCount(8, expected, "the seven files and the root directory");
        Assert.AreSequenceEqual(expected, Names(null, NameMatchMode.Exact));
        Assert.AreSequenceEqual(expected, Names(null, NameMatchMode.Glob));
    }

    [TestMethod]
    public void EmptyPattern_MatchesEveryNameWhenSubstringAndOnlyAnEmptyNameOtherwise()
    {
        Assert.HasCount(8, Names(string.Empty, NameMatchMode.Substring));
        Assert.AreSequenceEqual([string.Empty], Names(string.Empty, NameMatchMode.Exact));
        Assert.AreSequenceEqual([string.Empty], Names(string.Empty, NameMatchMode.Glob));
    }

    [TestMethod]
    public void UndefinedMode_ThrowsBeforeAnyRowIsRead()
    {
        var query = new SearchQuery(null, (NameMatchMode)99);

        var searchFailure = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SearchEngine.Search(_rootBlock, query, TestContext.CancellationToken));

        Assert.AreEqual("query", searchFailure.ParamName);
        Assert.StartsWith("Unknown name match mode.", searchFailure.Message);
    }

    public TestContext TestContext { get; set; } = null!;
}
