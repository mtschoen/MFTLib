using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Support = MFTLib.Tests.Index.PostingQueryTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>The multi-name exact query: result i is the single-name exact search for names[i], from one gather.</summary>
[TestClass]
public sealed class SearchExactNamesTests
{
    static readonly string[] Requested =
    [
        "index.js", "INDEX.JS", "Makefile", "absent.bin", "", "index.js", "makefile", "\uD800lone.bin", "café.txt",
        "Interned-Later-3.Dat"
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchExactNames_EqualsASingleNameSearchForEveryRequestedName(bool churn)
    {
        using var corpus = Support.BuildCorpus(seed: 31);
        if (churn)
        {
            Support.Churn(corpus, seed: 31, changes: 300);
        }

        using var opened = new Support.OpenedRoot(corpus.Builder);
        var root = opened.Root;
        SearchQuery[] filters =
        [
            new SearchQuery(null), new SearchQuery(null, CaseSensitive: true), new SearchQuery(null, IsDirectory: false),
            new SearchQuery(null, IncludeDeleted: true, SizeMinimum: 1000),
            new SearchQuery(null, Subtree: FileEntry.Create(root, 5), IncludeDeleted: true),
            new SearchQuery(null, LastWriteTimeMinimum: Support.Moment.AddDays(30))
        ];
        foreach (var filter in filters)
        {
            var results = SearchEngine.SearchExactNames(root, Requested, filter,
                cancellationToken: TestContext.CancellationToken);

            Assert.HasCount(Requested.Length, results);
            for (var index = 0; index < Requested.Length; index++)
            {
                var single = SearchEngine.Search(root,
                    filter with { NamePattern = Requested[index], MatchMode = NameMatchMode.Exact },
                    cancellationToken: TestContext.CancellationToken);
                Assert.AreSequenceEqual(single.Select(entry => entry.RowIndex).ToArray(),
                    results[index].Select(entry => entry.RowIndex).ToArray(), $"{Requested[index]} with {filter}");
            }
        }
    }

    [TestMethod]
    public void SearchExactNames_WithNoNamesOrNoBlock_ReturnsEmptyResultsOfTheRequestedShape()
    {
        using var corpus = Support.BuildCorpus(seed: 32, rowCount: 200);
        using var opened = new Support.OpenedRoot(corpus.Builder);

        Assert.IsEmpty(SearchEngine.SearchExactNames(opened.Root, [], new SearchQuery(null),
            cancellationToken: TestContext.CancellationToken));
        var blockless = SearchEngine.SearchExactNames(null, ["a", "b"], new SearchQuery(null),
            cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(2, blockless);
        Assert.IsTrue(blockless.All(list => list.Count == 0));
    }

    [TestMethod]
    public async Task FileIndexSearchExactNames_EqualsSearchPerName_AndTakesOneBorrow()
    {
        using var fixture = new TwoRootQueryFixture();
        await using var index = await fixture.OpenAsync();
        string[] names = ["readme.md", "README.MD", "report.pdf", "missing.txt", "readme.md"];

        var results = index.SearchExactNames(names, null, TestContext.CancellationToken);

        Assert.HasCount(names.Length, results);
        for (var position = 0; position < names.Length; position++)
        {
            var single = index.Search(new SearchQuery(names[position], NameMatchMode.Exact), TestContext.CancellationToken);
            Assert.AreSequenceEqual(single.Select(entry => entry.RowIndex).ToArray(),
                results[position].Select(entry => entry.RowIndex).ToArray(), names[position]);
        }

        Assert.IsNotEmpty(results[0]);
        Assert.AreEqual(0, index.CurrentBlock!.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public async Task FileIndexSearchExactNames_ValidatesItsArgumentsBeforeBorrowing()
    {
        using var fixture = new TwoRootQueryFixture();
        await using var index = await fixture.OpenAsync();

        Assert.ThrowsExactly<ArgumentNullException>(() => index.SearchExactNames(null!, null, TestContext.CancellationToken));
        var nullElement = Assert.ThrowsExactly<ArgumentException>(() =>
            index.SearchExactNames(["a", null!], null, TestContext.CancellationToken));
        StringAssert.Contains(nullElement.Message, "index 1");
        Assert.ThrowsExactly<ArgumentException>(() =>
            index.SearchExactNames(["a"], new SearchQuery("pattern"), TestContext.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() =>
            index.SearchExactNames(["a"], new SearchQuery(null, Subtree: default(FileEntry)), TestContext.CancellationToken));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            index.SearchExactNames(["a"], null, new CancellationToken(true)));
        Assert.IsEmpty(index.SearchExactNames([], null, TestContext.CancellationToken));
        Assert.AreEqual(0, index.CurrentBlock!.ReleaseState.Hooks.OutstandingBorrowCount);
    }
}
