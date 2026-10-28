using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSetTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task Constructor_ValidatesArgumentsAndCopiesList()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new FileIndexSet(null!));

        using var fixture = new TwoRootQueryFixture();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenSecondAsync();

        var nullElement = Assert.ThrowsExactly<ArgumentException>(() => new FileIndexSet([first, null!]));
        Assert.AreEqual("indices", nullElement.ParamName);

        var duplicate = Assert.ThrowsExactly<ArgumentException>(() => new FileIndexSet([first, first]));
        Assert.AreEqual("indices", duplicate.ParamName);
        Assert.Contains("Root T is listed more than once.", duplicate.Message);

        var mutable = new List<FileIndex> { first, second };
        var set = new FileIndexSet(mutable);
        mutable.Clear();
        Assert.AreSequenceEqual([first, second], set.Indices);

        var empty = new FileIndexSet([]);
        Assert.IsEmpty(empty.Indices);
        Assert.IsNull(empty.Find(Path.Combine(fixture.FirstRoot, "Documents", "readme.md"), Token));
        Assert.IsEmpty(empty.Search(new SearchQuery(null), Token));
        Assert.IsEmpty(empty.Enumerate(new SearchQuery(null), Token).ToArray());
        var rowCount = 0;
        foreach (var _ in empty.EnumerateRows(new SearchQuery(null), Token))
        {
            rowCount++;
        }

        Assert.AreEqual(0, rowCount);
    }

    static Task<FileIndex> OpenWalkAsync(string directory, CancellationToken token) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(directory),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled
        }, token);

    static void NestedWorkspace(string workspace, out string outerPath, out string innerPath)
    {
        outerPath = Path.Combine(workspace, "src");
        innerPath = Path.Combine(outerPath, "deeper");
        Directory.CreateDirectory(innerPath);
        File.WriteAllText(Path.Combine(innerPath, "inner.txt"), "x");
        File.WriteAllText(Path.Combine(outerPath, "outer.txt"), "x");
    }

    [TestMethod]
    public async Task NestedMembers_ReturnEachFileOnceAttributedToTheOutermost()
    {
        using var temp = new TempDirectory("mftlib-set-nested");
        NestedWorkspace(temp.Path, out var outerPath, out var innerPath);
        await using var outer = await OpenWalkAsync(outerPath, Token);
        await using var inner = await OpenWalkAsync(innerPath, Token);
        foreach (var set in new[] { new FileIndexSet([outer, inner]), new FileIndexSet([inner, outer]) })
        {
            var query = new SearchQuery(".txt");
            var found = set.Search(query, Token);
            Assert.AreSequenceEqual(["inner.txt", "outer.txt"], [.. found.Select(entry => entry.Name)], SequenceOrder.InAnyOrder);
            Assert.IsTrue(found.All(entry => entry.Root == outer.Status.Root));
            Assert.HasCount(2, set.Enumerate(query, Token).ToArray());
            var rows = 0;
            foreach (var _ in set.EnumerateRows(query, Token))
            {
                rows++;
            }

            Assert.AreEqual(2, rows);
            var innerFile = Path.Combine(innerPath, "inner.txt");
            Assert.AreEqual(found.Single(entry => entry.Name == "inner.txt"), set.Find(innerFile, Token));
        }
    }

    [TestMethod]
    public async Task NestedMembers_SharingAPrefixButNotASegment_AreNotNested()
    {
        using var temp = new TempDirectory("mftlib-set-prefix");
        NestedWorkspace(temp.Path, out var outerPath, out _);
        var siblingPath = outerPath + "2";
        Directory.CreateDirectory(siblingPath);
        await File.WriteAllTextAsync(Path.Combine(siblingPath, "sibling.txt"), "x", Token);
        await using var outer = await OpenWalkAsync(outerPath, Token);
        await using var sibling = await OpenWalkAsync(siblingPath, Token);
        var set = new FileIndexSet([outer, sibling]);

        var roots = set.Search(new SearchQuery(".txt"), Token).Select(entry => entry.Root).Distinct().ToArray();

        Assert.HasCount(2, roots);
    }

    [TestMethod]
    public async Task NestedMembers_RootsThatDifferOnlyInCase_AreNestedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Case folding of roots applies on Windows only.");
        }

        using var temp = new TempDirectory("mftlib-set-case");
        NestedWorkspace(temp.Path, out var outerPath, out var innerPath);
        await using var outer = await OpenWalkAsync(outerPath.ToUpperInvariant(), Token);
        await using var inner = await OpenWalkAsync(innerPath, Token);
        var set = new FileIndexSet([outer, inner]);

        Assert.HasCount(2, set.Search(new SearchQuery(".txt"), Token));
    }

    [TestMethod]
    public async Task NestedMembers_UnavailableOuter_LetsTheInnerCoverItsOwnSubtree()
    {
        using var temp = new TempDirectory("mftlib-set-outer-down");
        NestedWorkspace(temp.Path, out _, out var innerPath);
        await using var outer = await FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheMode = CacheMode.Disabled
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), temp.Path, 1)), Token);
        await using var inner = await OpenWalkAsync(innerPath, Token);
        Assert.IsNull(outer.Status.Block);
        var set = new FileIndexSet([outer, inner]);

        var found = set.Search(new SearchQuery(".txt"), Token);

        Assert.AreSequenceEqual(["inner.txt"], [.. found.Select(entry => entry.Name)]);
        Assert.AreEqual(inner.Status.Root, found[0].Root);
        Assert.AreEqual(found[0], set.Find(Path.Combine(innerPath, "inner.txt"), Token));
    }

    [TestMethod]
    public async Task Constructor_AcceptsSiblingViews()
    {
        using var workspace = new TempDirectory("mftlib-set-siblings");
        var sourcePath = Path.Combine(workspace.Path, "src");
        var otherPath = Path.Combine(workspace.Path, "other");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(otherPath);

        await using var source = await OpenWalkAsync(sourcePath, Token);
        await using var other = await OpenWalkAsync(otherPath, Token);

        var set = new FileIndexSet([source, other]);
        Assert.HasCount(2, set.Indices);
    }

    [TestMethod]
    public async Task Find_RoutesToTheOwningMemberAndDoesNotFallBackOnMissOrBlocklessMember()
    {
        using var rootDir = new TempDirectory("mftlib-set-find");
        var volumeRoot = Path.Combine(rootDir.Path, "volume");
        var repoRoot = Path.Combine(rootDir.Path, "work", "repo");
        Directory.CreateDirectory(Path.Combine(volumeRoot, "work"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(volumeRoot, "work", "other.txt"), "volume-file", Token);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "program.cs"), "repo-file", Token);

        await using var volumeIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheMode = CacheMode.Disabled
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), volumeRoot, 1, ProducerKind.Enumeration)), Token);
        await using var repoIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(repoRoot),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled
        }, Token);

        var set = new FileIndexSet([volumeIndex, repoIndex]);

        var inRepo = set.Find(Path.Combine(repoRoot, "src", "program.cs"), Token);
        Assert.IsNotNull(inRepo);
        Assert.AreEqual(repoIndex.Status.Root, inRepo.Value.Root);
        Assert.AreEqual("program.cs", inRepo.Value.Name);

        var inVolume = set.Find(Path.Combine(volumeRoot, "work", "other.txt"), Token);
        Assert.IsNotNull(inVolume);
        Assert.AreEqual(new VolumeKey('T'), inVolume.Value.Root);
        Assert.AreEqual("other.txt", inVolume.Value.Name);

        // A path inside repoRoot that does not exist in repoIndex is a miss, not a lookup in another member.
        Assert.IsNull(set.Find(Path.Combine(repoRoot, "missing.txt"), Token));

        // A path outside every member root returns null without borrowing any member.
        Assert.IsNull(set.Find(Path.Combine(rootDir.Path, "outside", "file.txt"), Token));
        Assert.IsNull(set.Find(string.Empty, Token));

        Assert.ThrowsExactly<ArgumentNullException>(() => set.Find(null!, Token));
        Assert.IsNull(set.Find("relative/path.txt", Token));
    }

    [TestMethod]
    public async Task Find_WhenTheOwningMemberIsBlockless_ReturnsNull()
    {
        using var rootDir = new TempDirectory("mftlib-set-blockless");
        using var cacheDir = new TempDirectory("mftlib-set-cache");
        var volumeRoot = Path.Combine(rootDir.Path, "volume");
        var repoRoot = Path.Combine(rootDir.Path, "work", "repo");
        Directory.CreateDirectory(volumeRoot);
        Directory.CreateDirectory(repoRoot);
        var sharedFile = Path.Combine(repoRoot, "shared.txt");
        await File.WriteAllTextAsync(sharedFile, "visible-only-to-the-declined-member", Token);

        await using var volumeIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheMode = CacheMode.Disabled
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), volumeRoot, 1, ProducerKind.Enumeration)), Token);
        await using var declinedRepoIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(repoRoot),
            Producer = ProducerSelection.WalkOnly,
            CacheDirectory = cacheDir.Path,
            CacheMode = CacheMode.ExistingOnly
        }, Token);
        Assert.AreEqual(RootState.Failed, declinedRepoIndex.Status.State);

        var set = new FileIndexSet([volumeIndex, declinedRepoIndex]);
        Assert.IsNull(set.Find(sharedFile, Token));
    }

    [TestMethod]
    public async Task Queries_FanOutInMemberOrderAndSkipBlocklessMembers()
    {
        using var fixture = new TwoRootQueryFixture();
        using var cacheDir = new TempDirectory("mftlib-set-fanout-cache");
        using var blocklessRoot = new TempDirectory("mftlib-set-fanout-blockless");
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenSecondAsync();
        await using var blockless = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheDirectory = cacheDir.Path,
            CacheMode = CacheMode.ExistingOnly
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('V'), blocklessRoot.Path, 3)), Token);

        var forward = new FileIndexSet([first, blockless, second]);
        var reverse = new FileIndexSet([second, blockless, first]);
        var query = new SearchQuery("readme.md", NameMatchMode.Exact);

        var forwardRoots = forward.Search(query, Token).Select(entry => entry.Root).ToArray();
        var reverseRoots = reverse.Search(query, Token).Select(entry => entry.Root).ToArray();
        Assert.AreSequenceEqual([new VolumeKey('T'), new VolumeKey('T'), new VolumeKey('U')], forwardRoots);
        Assert.AreSequenceEqual([new VolumeKey('U'), new VolumeKey('T'), new VolumeKey('T')], reverseRoots);
        Assert.AreSequenceEqual(forwardRoots, [.. forward.Enumerate(query, Token).Select(entry => entry.Root)]);
        Assert.AreSequenceEqual(reverseRoots, [.. reverse.Enumerate(query, Token).Select(entry => entry.Root)]);

        var rowRoots = new List<RootKey>();
        foreach (var row in reverse.EnumerateRows(query, Token))
        {
            rowRoots.Add(row.Root);
        }

        Assert.AreSequenceEqual(reverseRoots, rowRoots);
    }

    [TestMethod]
    public async Task SubtreeQueries_RouteOnlyToOwningMemberAndValidateForeignDefaultAndDisposedAnchors()
    {
        using var fixture = new TwoRootQueryFixture();
        using var foreignFixture = new TwoRootQueryFixture();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenSecondAsync();
        var foreign = await foreignFixture.OpenAsync();

        var set = new FileIndexSet([first, second]);
        var secondPictures = set.Find(Path.Combine(fixture.SecondRoot, "Pictures"), Token)!.Value;

        var firstAdmitted = 0;
        first.Hooks.ReaderAdmitted = () => firstAdmitted++;

        var matches = set.Search(new SearchQuery("readme.md", NameMatchMode.Exact, Subtree: secondPictures), Token);
        Assert.HasCount(1, matches);
        Assert.AreEqual(new VolumeKey('U'), matches[0].Root);
        Assert.AreEqual(0, firstAdmitted, "subtree on U must not borrow T");

        var enumerated = set.Enumerate(new SearchQuery("readme.md", NameMatchMode.Exact, Subtree: secondPictures), Token).ToArray();
        Assert.HasCount(1, enumerated);
        Assert.AreEqual(0, firstAdmitted);

        var rowCount = 0;
        foreach (var row in set.EnumerateRows(new SearchQuery("readme.md", NameMatchMode.Exact, Subtree: secondPictures), Token))
        {
            Assert.AreEqual(new VolumeKey('U'), row.Root);
            rowCount++;
        }

        Assert.AreEqual(1, rowCount);
        Assert.AreEqual(0, firstAdmitted);

        // Default FileEntry is rejected at call time.
        var defaultQuery = new SearchQuery(null, Subtree: default(FileEntry));
        Assert.ThrowsExactly<ArgumentException>(() => set.Search(defaultQuery, Token));
        Assert.ThrowsExactly<ArgumentException>(() => set.Enumerate(defaultQuery, Token));
        Assert.ThrowsExactly<ArgumentException>(() => { _ = set.EnumerateRows(defaultQuery, Token); });

        // Foreign FileEntry (from an index not in the set, both matching and unconfigured root keys) is rejected.
        var singleRootSet = new FileIndexSet([first]);
        var unconfiguredRootQuery = new SearchQuery(null, Subtree: secondPictures);
        Assert.ThrowsExactly<ArgumentException>(() => singleRootSet.Search(unconfiguredRootQuery, Token));
        Assert.ThrowsExactly<ArgumentException>(() => singleRootSet.Enumerate(unconfiguredRootQuery, Token).ToArray());
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            foreach (var _ in singleRootSet.EnumerateRows(unconfiguredRootQuery, Token))
            {
            }
        });

        var foreignEntry = foreign.Root(Token);
        var foreignQuery = new SearchQuery(null, Subtree: foreignEntry);
        Assert.ThrowsExactly<ArgumentException>(() => set.Search(foreignQuery, Token));
        Assert.ThrowsExactly<ArgumentException>(() => set.Enumerate(foreignQuery, Token).ToArray());
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            foreach (var _ in set.EnumerateRows(foreignQuery, Token))
            {
            }
        });

        // Disposed foreign anchor throws ObjectDisposedException.
        await foreign.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => set.Search(foreignQuery, Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => set.Enumerate(foreignQuery, Token).ToArray());
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            foreach (var _ in set.EnumerateRows(foreignQuery, Token))
            {
            }
        });
    }

    [TestMethod]
    public async Task CallTimeValidationAndCancellation_RejectInvalidInputs()
    {
        using var fixture = new TwoRootQueryFixture();
        await using var first = await fixture.OpenAsync();
        var set = new FileIndexSet([first]);

        Assert.ThrowsExactly<ArgumentNullException>(() => set.Search(null!, Token));
        Assert.ThrowsExactly<ArgumentNullException>(() => set.Enumerate(null!, Token));
        Assert.ThrowsExactly<ArgumentNullException>(() => { _ = set.EnumerateRows(null!, Token); });

        var invalidMode = new SearchQuery("readme", (NameMatchMode)99);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => set.Search(invalidMode, Token));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => set.Enumerate(invalidMode, Token));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => { _ = set.EnumerateRows(invalidMode, Token); });

        var cancelled = new CancellationToken(canceled: true);
        Assert.ThrowsExactly<OperationCanceledException>(() => set.Find(Path.Combine(fixture.FirstRoot, "Documents", "readme.md"), cancelled));
        Assert.ThrowsExactly<OperationCanceledException>(() => set.Search(new SearchQuery(null), cancelled));
        Assert.ThrowsExactly<OperationCanceledException>(() => set.Enumerate(new SearchQuery(null), cancelled).ToArray());
        Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            foreach (var _ in set.EnumerateRows(new SearchQuery(null), cancelled))
            {
            }
        });
    }
}
