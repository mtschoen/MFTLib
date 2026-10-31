using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>The public multi-name exact search on a set: result i equals the single-name exact Search for names[i].</summary>
[TestClass]
public sealed class FileIndexSetExactNamesTests
{
    static readonly string[] Requested = ["HEAD", "head", "objects", "refs", "absent", "", "HEAD", "Makefile"];

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    static Task<FileIndex> OpenWalkAsync(string directory, CancellationToken token) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(directory),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled
        }, token);

    static void Populate(string root)
    {
        foreach (var directory in new[] { "repo.git/objects", "repo.git/refs", "other/objects", "other/inner/refs" })
        {
            Directory.CreateDirectory(Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)));
        }

        foreach (var file in new[] { "repo.git/HEAD", "other/head", "other/inner/HEAD", "Makefile", "other/Makefile" })
        {
            File.WriteAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)), "x");
        }
    }

    static uint[] Rows(IEnumerable<FileEntry> entries) => [.. entries.Select(entry => entry.RowIndex)];

    static void AssertEquivalent(FileIndexSet set, string[] names, SearchQuery? filter, CancellationToken token)
    {
        var results = set.SearchExactNames(names, filter, token);
        Assert.HasCount(names.Length, results);
        for (var position = 0; position < names.Length; position++)
        {
            var single = set.Search((filter ?? new SearchQuery(null)) with
            {
                NamePattern = names[position],
                MatchMode = NameMatchMode.Exact
            }, token);
            Assert.AreSequenceEqual(single.Select(entry => (entry.Root, entry.RowIndex)).ToArray(),
                results[position].Select(entry => (entry.Root, entry.RowIndex)).ToArray(), names[position]);
        }
    }

    [TestMethod]
    public async Task SearchExactNames_AcrossSiblingAndNestedMembers_EqualsPerNameSearch()
    {
        using var temp = new TempDirectory("mftlib-set-exact");
        var first = Path.Combine(temp.Path, "first");
        var second = Path.Combine(temp.Path, "second");
        Populate(first);
        Populate(second);
        await using var outer = await OpenWalkAsync(first, Token);
        await using var sibling = await OpenWalkAsync(second, Token);
        await using var inner = await OpenWalkAsync(Path.Combine(first, "other"), Token);
        var set = new FileIndexSet([inner, outer, sibling]);

        var anchor = sibling.Find(Path.Combine(second, "other"), Token)!.Value;
        SearchQuery?[] filters =
        [
            null, new SearchQuery(null, CaseSensitive: true), new SearchQuery(null, IsDirectory: true),
            new SearchQuery(null, IsDirectory: false), new SearchQuery(null, Subtree: anchor)
        ];
        foreach (var filter in filters)
        {
            AssertEquivalent(set, Requested, filter, Token);
        }

        var results = set.SearchExactNames(Requested, null, Token);
        Assert.IsNotEmpty(results[0]);
        Assert.AreSequenceEqual(Rows(results[0]), Rows(results[6]));
        Assert.IsEmpty(results[4]);
        Assert.IsEmpty(results[5]);
        Assert.IsTrue(results[0].Select(entry => entry.Root).Distinct().Count() > 1);
    }

    [TestMethod]
    public async Task SearchExactNames_FilterMatchModeIsIgnored()
    {
        using var temp = new TempDirectory("mftlib-set-exact-mode");
        Populate(temp.Path);
        await using var index = await OpenWalkAsync(temp.Path, Token);
        var set = new FileIndexSet([index]);

        var viaGlob = set.SearchExactNames(["HEAD"], new SearchQuery(null, NameMatchMode.Glob), Token);
        var plain = set.SearchExactNames(["HEAD"], null, Token);

        Assert.AreSequenceEqual(Rows(plain[0]), Rows(viaGlob[0]));
    }

    [TestMethod]
    public async Task SearchExactNames_ValidatesArguments()
    {
        using var temp = new TempDirectory("mftlib-set-exact-args");
        Populate(temp.Path);
        await using var index = await OpenWalkAsync(temp.Path, Token);
        var set = new FileIndexSet([index]);

        Assert.ThrowsExactly<ArgumentNullException>(() => set.SearchExactNames(null!, null, Token));
        var nullName = Assert.ThrowsExactly<ArgumentException>(() => set.SearchExactNames(["HEAD", null!], null, Token));
        Assert.AreEqual("names", nullName.ParamName);
        Assert.Contains("index 1", nullName.Message);
        var named = Assert.ThrowsExactly<ArgumentException>(
            () => set.SearchExactNames(["HEAD"], new SearchQuery("HEAD"), Token));
        Assert.AreEqual("filter", named.ParamName);
        Assert.ThrowsExactly<ArgumentException>(
            () => set.SearchExactNames(["HEAD"], new SearchQuery(null, Subtree: default(FileEntry)), Token));
    }

    [TestMethod]
    public async Task SearchExactNames_EmptyList_ReturnsEmptyAfterACancellationCheck()
    {
        using var temp = new TempDirectory("mftlib-set-exact-empty");
        Populate(temp.Path);
        await using var index = await OpenWalkAsync(temp.Path, Token);
        var set = new FileIndexSet([index]);

        Assert.IsEmpty(set.SearchExactNames([], null, Token));
        Assert.IsEmpty(new FileIndexSet([]).SearchExactNames(["HEAD"], null, Token).SelectMany(list => list));
        Assert.HasCount(1, new FileIndexSet([]).SearchExactNames(["HEAD"], null, Token));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.ThrowsExactly<OperationCanceledException>(() => set.SearchExactNames([], null, cancelled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => set.SearchExactNames(["HEAD"], null, cancelled.Token));
    }

    [TestMethod]
    public async Task SearchExactNames_DisposedMember_Throws()
    {
        using var temp = new TempDirectory("mftlib-set-exact-disposed");
        Populate(temp.Path);
        var index = await OpenWalkAsync(temp.Path, Token);
        var set = new FileIndexSet([index]);
        await index.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => set.SearchExactNames(["HEAD"], null, Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => set.SearchExactNames([], null, Token));
    }
}
