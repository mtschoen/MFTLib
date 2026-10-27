using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#774: a query borrows only the root blocks it reads, so a query held on one root of a
///     multi-root index never keeps another root's retired block mapped. An unscoped query still
///     borrows every current block.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexPerQueryBorrowTests : IndexTreeTestBase
{
    const int CollectionPassLimit = 50;

    TempDirectory? _secondTree;
    uint _secondSerial;
    int _rescanCount;

    /// <summary>Creates two isolated enumeration trees and a shared cache location.</summary>
    [TestInitialize]
    public void Initialize()
    {
        _secondSerial = TestVolumeSerial.GetNext();
        _secondTree = new TempDirectory("mftlib-tree");
        File.WriteAllText(Path.Combine(_secondTree.Path, "unchanged.md"), "unchanged");
    }

    /// <summary>Removes the trees and cached blocks.</summary>
    public override void Dispose()
    {
        try
        {
            _secondTree?.Dispose();
        }
        finally
        {
            base.Dispose();
        }
    }

    public TestContext TestContext { get; set; } = null!;

    FileIndexOptions FirstRootOptions() => new()
    {
        Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, UniqueVolumeSerial, ProducerKind.Enumeration),
        CacheDirectory = CacheDirectoryPath
    };

    FileIndexOptions SecondRootOptions() => new()
    {
        Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), _secondTree!.Path, _secondSerial, ProducerKind.Enumeration),
        CacheDirectory = CacheDirectoryPath
    };

    string ReadmePath => Path.Combine(TreeRoot, "Documents", "readme.md");

    [MethodImpl(MethodImplOptions.NoInlining)]
    static RootBlockRelease ReleaseStateOf(FileIndex index) =>
        index.CurrentBlock!.ReleaseState;

    async Task RescanSecondRootAsync(FileIndex secondIndex)
    {
        await File.WriteAllTextAsync(Path.Combine(_secondTree!.Path, $"added-{++_rescanCount}.md"), "added",
            TestContext.CancellationToken);
        await secondIndex.RescanAsync(TestContext.CancellationToken);
    }

    /// <summary>Collects until the release completes or the pass limit is reached; no sleeping or timing.</summary>
    static bool CollectUntilReleased(RootBlockRelease release, int passes = CollectionPassLimit)
    {
        for (var pass = 0; pass < passes && !release.IsReleaseComplete; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return release.IsReleaseComplete;
    }

    /// <summary>
    ///     Runs <paramref name="query" /> on a pool thread parked once its borrow on <paramref name="firstIndex" /> is counted,
    ///     rescans root U and collects from this thread, then lets the query finish. Returns whether U's retired block was
    ///     released while the query still held its borrow.
    /// </summary>
    async Task<bool> RescanWhileQueryHeldAsync(FileIndex firstIndex, FileIndex secondIndex, RootBlockRelease retired, Action query,
        int passes = CollectionPassLimit, Action? onAdmitted = null)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstIndex.Hooks.ReaderAdmitted = () =>
        {
            onAdmitted?.Invoke();
            admitted.TrySetResult();
            finish.Task.Wait(TestContext.CancellationToken);
        };
        var running = Task.Run(query, TestContext.CancellationToken);
        try
        {
            await admitted.Task.WaitAsync(TestContext.CancellationToken);
            await RescanSecondRootAsync(secondIndex);
            return CollectUntilReleased(retired, passes);
        }
        finally
        {
            finish.TrySetResult();
            await running;
        }
    }

    async Task<(FileIndex FirstIndex, FileIndex SecondIndex, FileIndexSet Set, FileEntry FirstRoot)> OpenAsync()
    {
        var firstIndex = await FileIndex.OpenAsync(FirstRootOptions(), TestContext.CancellationToken);
        var secondIndex = await FileIndex.OpenAsync(SecondRootOptions(), TestContext.CancellationToken);
        var set = new FileIndexSet([firstIndex, secondIndex]);
        return (firstIndex, secondIndex, set, firstIndex.Root(TestContext.CancellationToken));
    }

    /// <summary>A suspended Enumerate anchored on root T does not delay root U's retired block.</summary>
    [TestMethod]
    public async Task Enumerate_AnchoredOnOneRoot_DoesNotKeepAnotherRootsRetiredBlockMapped()
    {
        var (firstIndex, secondIndex, set, root) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var retired = ReleaseStateOf(secondIndex);
        using var enumerator = set.Enumerate(new SearchQuery("", Subtree: root), TestContext.CancellationToken)
            .GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());

        await RescanSecondRootAsync(secondIndex);

        Assert.IsTrue(CollectUntilReleased(retired), "an Enumerate on root T kept root U's retired block mapped");
        Assert.AreEqual(0, retired.Hooks.OutstandingBorrowCount);
        Assert.IsTrue(enumerator.MoveNext());
    }

    /// <summary>An EnumerateRows anchored on root T does not delay root U's retired block.</summary>
    [TestMethod]
    public async Task EnumerateRows_AnchoredOnOneRoot_DoesNotKeepAnotherRootsRetiredBlockMapped()
    {
        var (firstIndex, secondIndex, set, root) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var retired = ReleaseStateOf(secondIndex);

        var released = await RescanWhileQueryHeldAsync(firstIndex, secondIndex, retired, () =>
        {
            foreach (var row in set.EnumerateRows(new SearchQuery("", Subtree: root), TestContext.CancellationToken))
            {
                _ = row.Name.Length;
            }
        });

        Assert.IsTrue(released, "an EnumerateRows on root T kept root U's retired block mapped");
    }

    /// <summary>Find on a root T path does not delay root U's retired block.</summary>
    [TestMethod]
    public async Task Find_OnOneRoot_DoesNotKeepAnotherRootsRetiredBlockMapped()
    {
        var (firstIndex, secondIndex, set, _) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var retired = ReleaseStateOf(secondIndex);

        var released = await RescanWhileQueryHeldAsync(firstIndex, secondIndex, retired,
            () => _ = set.Find(ReadmePath, TestContext.CancellationToken));

        Assert.IsTrue(released, "a Find on root T kept root U's retired block mapped");
    }

    /// <summary>Root of root T does not delay root U's retired block.</summary>
    [TestMethod]
    public async Task Root_OfOneRoot_DoesNotKeepAnotherRootsRetiredBlockMapped()
    {
        var (firstIndex, secondIndex, _, _) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var retired = ReleaseStateOf(secondIndex);

        var released = await RescanWhileQueryHeldAsync(firstIndex, secondIndex, retired,
            () => _ = firstIndex.Root(TestContext.CancellationToken));

        Assert.IsTrue(released, "a Root on root T kept root U's retired block mapped");
    }

    /// <summary>A materialized anchored Search does not delay root U's retired block.</summary>
    [TestMethod]
    public async Task Search_AnchoredOnOneRoot_DoesNotKeepAnotherRootsRetiredBlockMapped()
    {
        var (firstIndex, secondIndex, set, root) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var retired = ReleaseStateOf(secondIndex);

        var released = await RescanWhileQueryHeldAsync(firstIndex, secondIndex, retired,
            () => _ = set.Search(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken));

        Assert.IsTrue(released, "a Search anchored on root T kept root U's retired block mapped");
    }

    /// <summary>Each scoped query counts exactly one borrow on the block it reads and none on the other root.</summary>
    [TestMethod]
    public async Task ScopedQueries_BorrowOnlyTheBlockTheyRead()
    {
        var (firstIndex, secondIndex, set, root) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var firstRelease = ReleaseStateOf(firstIndex);
        var secondRelease = ReleaseStateOf(secondIndex);
        var observed = new List<(int First, int Second)>();
        firstIndex.Hooks.ReaderAdmitted = () =>
            observed.Add((firstRelease.Hooks.OutstandingBorrowCount, secondRelease.Hooks.OutstandingBorrowCount));

        _ = set.Find(ReadmePath, TestContext.CancellationToken);
        _ = firstIndex.Root(TestContext.CancellationToken);
        _ = set.Search(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken);
        _ = set.Enumerate(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken).ToList();
        foreach (var row in set.EnumerateRows(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken))
        {
            _ = row.Name.Length;
        }

        Assert.HasCount(5, observed);
        CollectionAssert.AreEqual(Enumerable.Repeat((1, 0), 5).ToList(), observed);
    }

    /// <summary>An unscoped query borrows one member at a time, so another member's retired block can release while the first is scanned.</summary>
    [TestMethod]
    public async Task UnscopedSearch_BorrowsOnlyTheActiveMemberBlock()
    {
        var (firstIndex, secondIndex, set, _) = await OpenAsync();
        await using var disposableFirst = firstIndex;
        await using var disposableSecond = secondIndex;
        var firstRelease = ReleaseStateOf(firstIndex);
        var secondRelease = ReleaseStateOf(secondIndex);
        (int First, int Second) observed = default;

        var released = await RescanWhileQueryHeldAsync(firstIndex, secondIndex, secondRelease,
            () => _ = set.Search(new SearchQuery("readme"), TestContext.CancellationToken), passes: 3,
            onAdmitted: () => observed = (firstRelease.Hooks.OutstandingBorrowCount,
                secondRelease.Hooks.OutstandingBorrowCount));

        Assert.AreEqual((1, 0), observed);
        Assert.IsTrue(released, "an unscoped Search on FileIndexSet must borrow one member at a time");
    }
}
