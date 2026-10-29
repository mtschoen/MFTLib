using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NamePool = MFTLibTestExtensions.SyntheticNamePool;
using Support = MFTLib.Tests.Index.PostingQueryTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     The core property of the posting query path: over synthetic blocks, an exact query answers exactly what the old row
///     pass answered, in the same order, for every operation, filter and case rule, including rows whose name changed
///     after the base was built.
/// </summary>
[TestClass]
public sealed class PostingQueryEquivalenceTests
{
    public TestContext TestContext { get; set; } = null!;

    static IEnumerable<Func<SearchQuery, SearchQuery>> Filters(RootBlock root)
    {
        yield return query => query;
        yield return query => query with { IsDirectory = false };
        yield return query => query with { IsDirectory = true };
        yield return query => query with { IncludeDeleted = true };
        yield return query => query with { SizeMinimum = 2000, SizeMaximum = 7000 };
        yield return query => query with
        {
            LastWriteTimeMinimum = Support.Moment.AddDays(20),
            LastWriteTimeMaximum = Support.Moment.AddDays(60)
        };
        yield return query => query with { Subtree = FileEntry.Create(root, 3), IncludeDeleted = true };
    }

    static IEnumerable<string> Patterns(Support.Corpus corpus)
    {
        var random = new Random(7);
        foreach (var name in Support.SpecialNames)
        {
            yield return name;
            yield return name.ToUpperInvariant();
        }

        for (var index = 0; index < 60; index++)
        {
            var name = corpus.Names[random.Next(corpus.Names.Count)];
            yield return name;
            yield return name.ToLowerInvariant();
        }

        foreach (var name in corpus.Names.Where(name => name.StartsWith("Interned-Later", StringComparison.Ordinal)).Distinct())
        {
            yield return name;
            yield return name.ToLowerInvariant();
        }

        yield return "";
        yield return "absent-name.bin";
        yield return "FILE1.DAT";
    }

    void AssertEquivalent(Support.Corpus corpus, string label)
    {
        using var opened = new Support.OpenedRoot(corpus.Builder);
        var root = opened.Root;
        var checkedQueries = 0;
        foreach (var pattern in Patterns(corpus))
        {
            foreach (var caseSensitive in new[] { false, true })
            {
                foreach (var filter in Filters(root))
                {
                    var query = filter(new SearchQuery(pattern, NameMatchMode.Exact, caseSensitive));
                    var expected = Support.ReferenceRows(root, query, TestContext.CancellationToken);
                    var searched = SearchEngine.Search(root, query, cancellationToken: TestContext.CancellationToken);
                    var streamed = SearchEngine.Enumerate(root, query, cancellationToken: TestContext.CancellationToken);
                    var viewed = NamePool.EnumerateRows(root, query, TestContext.CancellationToken);
                    var why = $"{label} pattern='{pattern}' caseSensitive={caseSensitive} query={query}";
                    Assert.AreSequenceEqual(expected, searched.Select(entry => entry.RowIndex).ToArray(), why);
                    Assert.AreSequenceEqual(expected, streamed.Select(entry => entry.RowIndex).ToArray(), why);
                    Assert.AreSequenceEqual(expected, viewed.Select(row => row.Row).ToArray(), why);
                    checkedQueries++;
                }
            }
        }

        Assert.IsGreaterThan(1500, checkedQueries);
    }

    [TestMethod]
    public void ExactQueries_OverAFreshlyBuiltBase_EqualTheRowPass()
    {
        using var corpus = Support.BuildCorpus(seed: 1);
        AssertEquivalent(corpus, "fresh");
    }

    [TestMethod]
    [DataRow(2, 150)]
    [DataRow(3, 600)]
    public void ExactQueries_AfterRenamesStaleAssociationsAndDeltas_EqualTheRowPass(int seed, int changes)
    {
        using var corpus = Support.BuildCorpus(seed);
        Support.Churn(corpus, seed, changes);
        Assert.IsGreaterThan(0u, corpus.Builder.OpenForWriting().DeltaEntryCount);
        AssertEquivalent(corpus, $"churned seed {seed}");
    }

    [TestMethod]
    public void ExactQuery_ForARowWhoseNewNameWasNeverAssociated_MissesItWhileASubstringQueryFindsIt()
    {
        // The posting path has no row-scan fallback: it answers from postings and verifies. A name the descriptor holds
        // without an association (which a writer that skips the append would leave) is therefore not found by an exact
        // query, which is why the journal appends before it stores the descriptor.
        using var builder = new SyntheticBlockBuilder(slotCapacity: 64, namePoolCapacity: 4096);
        var root = builder.AddRoot();
        builder.AddRow("a.txt", root, RowFlags.InUse, 1, Support.Moment, 0);
        var moved = builder.AddRow("b.txt", root, RowFlags.InUse, 1, Support.Moment, 0);
        builder.Complete(Support.Moment);
        Assert.IsTrue(MFTLib.Index.NamePool.TryFindOrdinal(builder.OpenForWriting(), "a.txt", out var firstId));
        builder.MutateNameDescriptor(moved, firstId);
        using var opened = new Support.OpenedRoot(builder);

        var exact = SearchEngine.Search(opened.Root, new SearchQuery("a.txt", NameMatchMode.Exact),
            cancellationToken: TestContext.CancellationToken);
        var substring = SearchEngine.Search(opened.Root, new SearchQuery("a.txt"),
            cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(new uint[] { 1 }, exact.Select(entry => entry.RowIndex).ToArray());
        Assert.AreSequenceEqual(new uint[] { 1, moved }, substring.Select(entry => entry.RowIndex).ToArray());
    }

    [TestMethod]
    public void ExactQuery_ForAnUnnamedRoot_FindsTheEmptyNameThroughItsOnlyRowPass()
    {
        using var corpus = Support.BuildCorpus(seed: 4);
        using var opened = new Support.OpenedRoot(corpus.Builder);

        var results = SearchEngine.Search(opened.Root, new SearchQuery("", NameMatchMode.Exact),
            cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(new uint[] { 0 }, results.Select(entry => entry.RowIndex).ToArray());
    }
}
