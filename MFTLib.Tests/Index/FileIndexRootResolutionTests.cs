using System.Diagnostics;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A directory root is opened under its final path, with junctions and symbolic links followed, so two keys for one
///     folder are duplicates in a set and an alias inside another member is nested. Seam cases choose what a path
///     resolves to; the real-link cases create a junction (Windows) or a symbolic link (elsewhere) in a temporary
///     directory and need no elevation.
/// </summary>
[TestClass]
public sealed class FileIndexRootResolutionTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    static FileIndexOptions Options(string directory, FinalPathResolver? resolver = null,
        Action<string>? diagnostics = null) =>
        new()
        {
            Root = RootKey.FromDirectory(directory),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled,
            FinalPath = resolver,
            Diagnostics = diagnostics
        };

    static string Folder(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    static FinalPathResolver MapsTo(string target, List<string>? calls = null) => path =>
    {
        calls?.Add(path);
        return new FinalPathResolution(target, null);
    };

    [TestMethod]
    public async Task AliasResolvingIntoAnotherMembersSubtree_IsNestedAndReadOnce()
    {
        using var temp = new TempDirectory("mftlib-resolve-nested");
        var outer = Folder(temp.Path, "outer");
        var real = Folder(outer, "real");
        await File.WriteAllTextAsync(Path.Combine(real, "a.txt"), "x", Token);
        var alias = Folder(temp.Path, "alias");
        await using var outerIndex = await FileIndex.OpenAsync(Options(outer), Token);
        await using var aliasIndex = await FileIndex.OpenAsync(Options(alias, MapsTo(real)), Token);
        var set = new FileIndexSet([aliasIndex, outerIndex]);

        var found = set.Search(new SearchQuery("a.txt"), Token);

        Assert.HasCount(1, found);
        Assert.AreEqual(outerIndex.RootKey, found[0].Root);
        Assert.AreEqual(RootKey.FromDirectory(alias), aliasIndex.RootKey);
        Assert.AreEqual(found[0], set.Find(Path.Combine(real, "a.txt"), Token));
    }

    [TestMethod]
    public async Task TwoAliasesOfOneFolder_AreDuplicatesInASet()
    {
        using var temp = new TempDirectory("mftlib-resolve-duplicate");
        var real = Folder(temp.Path, "real");
        var first = Folder(temp.Path, "first");
        var second = Folder(temp.Path, "second");
        await using var firstIndex = await FileIndex.OpenAsync(Options(first, MapsTo(real)), Token);
        await using var secondIndex = await FileIndex.OpenAsync(Options(second, MapsTo(real)), Token);

        var failure = Assert.ThrowsExactly<ArgumentException>(() => new FileIndexSet([firstIndex, secondIndex]));

        Assert.AreEqual("indices", failure.ParamName);
        Assert.Contains(first, failure.Message);
        Assert.Contains(second, failure.Message);
        Assert.Contains(real, failure.Message);
    }

    [TestMethod]
    public async Task ResolutionFailure_FallsBackToThePathAsNamedWithOneDiagnostic()
    {
        using var temp = new TempDirectory("mftlib-resolve-failure");
        var directory = Folder(temp.Path, "plain");
        var diagnostics = new List<string>();

        await using var index = await FileIndex.OpenAsync(
            Options(directory, _ => new FinalPathResolution(null, "error 3"), diagnostics.Add), Token);

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(directory, index.RootDirectory);
        Assert.HasCount(1, diagnostics);
        Assert.Contains(directory, diagnostics[0]);
        Assert.Contains("error 3", diagnostics[0]);
    }

    [TestMethod]
    public async Task ResolutionFailureWithAThrowingDiagnosticSink_StillOpens()
    {
        using var temp = new TempDirectory("mftlib-resolve-throwing-sink");
        var directory = Folder(temp.Path, "plain");

        await using var index = await FileIndex.OpenAsync(
            Options(directory, _ => new FinalPathResolution(null, "error 3"),
                _ => throw new InvalidOperationException("sink failed")), Token);

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(directory, index.RootDirectory);
    }

    [TestMethod]
    public async Task ResolutionToAPathThatIsNotAValidRoot_FallsBackWithOneDiagnostic()
    {
        using var temp = new TempDirectory("mftlib-resolve-invalid");
        var directory = Folder(temp.Path, "plain");
        var diagnostics = new List<string>();

        await using var index = await FileIndex.OpenAsync(
            Options(directory, _ => new FinalPathResolution("relative/path", null), diagnostics.Add), Token);

        Assert.AreEqual(directory, index.RootDirectory);
        Assert.HasCount(1, diagnostics);
    }

    [TestMethod]
    public async Task MissingDirectory_SettlesOfflineWithoutAskingTheResolver()
    {
        using var temp = new TempDirectory("mftlib-resolve-missing");
        var missing = Path.Combine(temp.Path, "later");
        var calls = new List<string>();

        await using var index = await FileIndex.OpenAsync(Options(missing, MapsTo(temp.Path, calls)), Token);

        Assert.AreEqual(RootState.Offline, index.Status.State);
        Assert.IsNull(index.Status.Producer);
        Assert.IsEmpty(calls);
        Assert.AreEqual(missing, index.RootDirectory);
    }

    [TestMethod]
    public async Task Rescan_ReusesTheResolutionOfTheOpen()
    {
        using var temp = new TempDirectory("mftlib-resolve-rescan");
        var named = Folder(temp.Path, "named");
        var real = Folder(temp.Path, "real");
        var calls = new List<string>();
        await using var index = await FileIndex.OpenAsync(Options(named, MapsTo(real, calls)), Token);

        await index.RescanAsync(Token);

        Assert.AreSequenceEqual([named], calls);
        Assert.AreEqual(real, index.RootDirectory);
    }

    [TestMethod]
    public async Task ReopenAfterTheLinkIsRepointed_DiscardsTheWalkBlockOfTheOldTarget()
    {
        using var temp = new TempDirectory("mftlib-resolve-repoint");
        var cache = Folder(temp.Path, "cache");
        var named = Folder(temp.Path, "named");
        var first = Folder(temp.Path, "first");
        var second = Folder(temp.Path, "second");
        await File.WriteAllTextAsync(Path.Combine(first, "old.txt"), "x", Token);
        await File.WriteAllTextAsync(Path.Combine(second, "new.txt"), "x", Token);
        var diagnostics = new List<string>();

        FileIndexOptions Persistent(string target) => Options(named, MapsTo(target), diagnostics.Add) with
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = cache
        };

        await using (var seeded = await FileIndex.OpenAsync(Persistent(first), Token))
        {
            Assert.HasCount(1, seeded.Search(new SearchQuery("old.txt"), Token));
        }

        await using var reopened = await FileIndex.OpenAsync(Persistent(second), Token);

        Assert.AreEqual(RootState.Ready, reopened.Status.State);
        Assert.IsEmpty(reopened.Search(new SearchQuery("old.txt"), Token));
        Assert.HasCount(1, reopened.Search(new SearchQuery("new.txt"), Token));
        Assert.Contains(message => message.Contains(nameof(BlockValidationResult.WrongRootDirectory), StringComparison.Ordinal),
            diagnostics);
    }

    [TestMethod]
    public async Task ReopenOnAnotherVolume_DoesNotAdoptTheBlockOfTheOldVolume()
    {
        using var temp = new TempDirectory("mftlib-resolve-volume");
        var cache = Folder(temp.Path, "cache");
        var view = Folder(temp.Path, "src");
        var scans = 0;
        var source = SyntheticIndexSource.Create(new SyntheticMftProducer(_ =>
        {
            scans++;
            return [new SyntheticRow(5, ".", 5) { IsDirectory = true }, new SyntheticRow(30, "src", 5) { IsDirectory = true }];
        })
        {
            JournalCursor = new SyntheticJournalCursor(7, 4096),
            ScanTimestamp = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc)
        });

        FileIndexOptions OnVolume(uint serial) => SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(view),
            CacheDirectory = cache,
            MftSource = source
        }, serial, volume: new VolumeKey('T'), volumeRootDirectory: temp.Path);

        await using (var seeded = await FileIndex.OpenAsync(OnVolume(1), Token))
        {
            Assert.AreEqual(ProducerKind.Mft, seeded.Status.Producer);
        }

        await using (await FileIndex.OpenAsync(OnVolume(1), Token))
        {
            Assert.AreEqual(1, scans);
        }

        await using var otherVolume = await FileIndex.OpenAsync(OnVolume(2), Token);

        Assert.AreEqual(2, scans);
        Assert.HasCount(2, Directory.GetFiles(cache, "*.mlix"));
    }

    [TestMethod]
    [DataRow(@"\\?\C:\work\src", @"C:\work\src")]
    [DataRow(@"\\?\D:\", @"D:\")]
    [DataRow(@"\\?\UNC\server\share\dir", @"\\server\share\dir")]
    [DataRow(@"C:\plain", @"C:\plain")]
    public void StripDosPrefix_MapsTheFinalPathToAHostPath(string finalPath, string expected) =>
        Assert.AreEqual(expected, PlatformFinalPath.StripDosPrefix(finalPath));

    [TestMethod]
    public void PlatformResolver_FollowsARealLinkAndReportsAMissingPathAsAFailure()
    {
        using var temp = new TempDirectory("mftlib-resolve-platform");
        var real = Folder(temp.Path, "real");
        var link = Path.Combine(temp.Path, "link");
        CreateLink(link, real);
        using var linkRemoval = new LinkRemoval(link);

        var viaLink = PlatformFinalPath.Resolve(link);
        var missing = PlatformFinalPath.Resolve(Path.Combine(temp.Path, "absent"));

        Assert.IsNull(viaLink.Failure);
        Assert.AreEqual(Path.GetFullPath(real), Path.GetFullPath(viaLink.Path!), ignoreCase: OperatingSystem.IsWindows());
        Assert.IsNull(missing.Path);
        Assert.IsNotNull(missing.Failure);
    }

    [TestMethod]
    public async Task RealLink_AndItsTargetAreDuplicates_AndALinkUnderAnotherMemberIsNested()
    {
        using var temp = new TempDirectory("mftlib-resolve-real");
        var outer = Folder(temp.Path, "outer");
        var real = Folder(outer, "real");
        await File.WriteAllTextAsync(Path.Combine(real, "a.txt"), "x", Token);
        var elsewhere = Folder(temp.Path, "elsewhere");
        var link = Path.Combine(elsewhere, "link");
        CreateLink(link, real);
        using var linkRemoval = new LinkRemoval(link);
        await using var realIndex = await FileIndex.OpenAsync(Options(real), Token);
        await using var linkIndex = await FileIndex.OpenAsync(Options(link), Token);
        await using var outerIndex = await FileIndex.OpenAsync(Options(outer), Token);

        Assert.ThrowsExactly<ArgumentException>(() => new FileIndexSet([realIndex, linkIndex]));
        var nested = new FileIndexSet([linkIndex, outerIndex]);
        var found = nested.Search(new SearchQuery("a.txt"), Token);

        Assert.HasCount(1, found);
        Assert.AreEqual(outerIndex.RootKey, found[0].Root);
    }

    // Removes only the link, not its target, so the temporary directory's recursive delete never crosses it.
    sealed class LinkRemoval(string link) : IDisposable
    {
        public void Dispose() => Directory.Delete(link);
    }

    // A junction on Windows (no privilege needed, unlike a symbolic link) and a symbolic link elsewhere.
    static void CreateLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "mklink /J failed");
    }
}
