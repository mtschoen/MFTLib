using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#774: a query borrows only the drive blocks it reads, so a query held on one drive of a
///     multi-drive index never keeps another drive's retired block mapped. An unscoped query still
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

    FileIndexOptions TwoDriveOptions() => new()
    {
        Drives = [
            MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('T'), TreeRoot, UniqueVolumeSerial, ProducerKind.Enumeration),
            MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('U'), _secondTree!.Path, _secondSerial, ProducerKind.Enumeration)
        ],
        CacheDirectory = CacheDirectoryPath
    };

    string ReadmePath => Path.Combine(TreeRoot, "Documents", "readme.md");

    [MethodImpl(MethodImplOptions.NoInlining)]
    static DriveBlockRelease ReleaseStateOf(FileIndex index, char drive) =>
        LookupEngine.FindDriveBlock(index.CurrentSnapshot, new VolumeKey(drive))!.ReleaseState;

    async Task RescanSecondDriveAsync(FileIndex index)
    {
        await File.WriteAllTextAsync(Path.Combine(_secondTree!.Path, $"added-{++_rescanCount}.md"), "added",
            TestContext.CancellationToken);
        await index.RescanAsync([new VolumeKey('U')], TestContext.CancellationToken);
    }

    /// <summary>Collects until the release completes or the pass limit is reached; no sleeping or timing.</summary>
    static bool CollectUntilReleased(DriveBlockRelease release, int passes = CollectionPassLimit)
    {
        for (var pass = 0; pass < passes && !release.IsReleaseComplete; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return release.IsReleaseComplete;
    }

    /// <summary>
    ///     Runs <paramref name="query" /> on a pool thread parked once its borrow is counted, rescans drive U and
    ///     collects from this thread, then lets the query finish. Returns whether U's retired block was released
    ///     while the query still held its borrow.
    /// </summary>
    async Task<bool> RescanWhileQueryHeldAsync(FileIndex index, DriveBlockRelease retired, Action query,
        int passes = CollectionPassLimit, Action? onAdmitted = null)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Hooks.ReaderAdmitted = () =>
        {
            onAdmitted?.Invoke();
            admitted.TrySetResult();
            finish.Task.Wait(TestContext.CancellationToken);
        };
        var running = Task.Run(query, TestContext.CancellationToken);
        try
        {
            await admitted.Task.WaitAsync(TestContext.CancellationToken);
            await RescanSecondDriveAsync(index);
            return CollectUntilReleased(retired, passes);
        }
        finally
        {
            finish.TrySetResult();
            await running;
        }
    }

    async Task<(FileIndex Index, FileEntry FirstRoot)> OpenAsync()
    {
        var index = await FileIndex.OpenAsync(TwoDriveOptions(), TestContext.CancellationToken);
        return (index, index.Root(new VolumeKey('T'), TestContext.CancellationToken));
    }

    /// <summary>A suspended Enumerate anchored on drive T does not delay drive U's retired block.</summary>
    [TestMethod]
    public async Task Enumerate_AnchoredOnOneDrive_DoesNotKeepAnotherDrivesRetiredBlockMapped()
    {
        var (index, root) = await OpenAsync();
        await using var disposable = index;
        var retired = ReleaseStateOf(index, 'U');
        using var enumerator = index.Enumerate(new SearchQuery("", Subtree: root), TestContext.CancellationToken)
            .GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());

        await RescanSecondDriveAsync(index);

        Assert.IsTrue(CollectUntilReleased(retired), "an Enumerate on drive T kept drive U's retired block mapped");
        Assert.AreEqual(0, retired.Hooks.OutstandingBorrowCount);
        Assert.IsTrue(enumerator.MoveNext());
    }

    /// <summary>An EnumerateRows anchored on drive T does not delay drive U's retired block.</summary>
    [TestMethod]
    public async Task EnumerateRows_AnchoredOnOneDrive_DoesNotKeepAnotherDrivesRetiredBlockMapped()
    {
        var (index, root) = await OpenAsync();
        await using var disposable = index;
        var retired = ReleaseStateOf(index, 'U');

        var released = await RescanWhileQueryHeldAsync(index, retired, () =>
        {
            foreach (var row in index.EnumerateRows(new SearchQuery("", Subtree: root), TestContext.CancellationToken))
            {
                _ = row.Name.Length;
            }
        });

        Assert.IsTrue(released, "an EnumerateRows on drive T kept drive U's retired block mapped");
    }

    /// <summary>Find on a drive T path does not delay drive U's retired block.</summary>
    [TestMethod]
    public async Task Find_OnOneDrive_DoesNotKeepAnotherDrivesRetiredBlockMapped()
    {
        var (index, _) = await OpenAsync();
        await using var disposable = index;
        var retired = ReleaseStateOf(index, 'U');

        var released = await RescanWhileQueryHeldAsync(index, retired,
            () => _ = index.Find(ReadmePath, TestContext.CancellationToken));

        Assert.IsTrue(released, "a Find on drive T kept drive U's retired block mapped");
    }

    /// <summary>Root of drive T does not delay drive U's retired block.</summary>
    [TestMethod]
    public async Task Root_OfOneDrive_DoesNotKeepAnotherDrivesRetiredBlockMapped()
    {
        var (index, _) = await OpenAsync();
        await using var disposable = index;
        var retired = ReleaseStateOf(index, 'U');

        var released = await RescanWhileQueryHeldAsync(index, retired,
            () => _ = index.Root(new VolumeKey('T'), TestContext.CancellationToken));

        Assert.IsTrue(released, "a Root on drive T kept drive U's retired block mapped");
    }

    /// <summary>A materialized anchored Search does not delay drive U's retired block.</summary>
    [TestMethod]
    public async Task Search_AnchoredOnOneDrive_DoesNotKeepAnotherDrivesRetiredBlockMapped()
    {
        var (index, root) = await OpenAsync();
        await using var disposable = index;
        var retired = ReleaseStateOf(index, 'U');

        var released = await RescanWhileQueryHeldAsync(index, retired,
            () => _ = index.Search(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken));

        Assert.IsTrue(released, "a Search anchored on drive T kept drive U's retired block mapped");
    }

    /// <summary>Each scoped query counts exactly one borrow on the block it reads and none on the other drive.</summary>
    [TestMethod]
    public async Task ScopedQueries_BorrowOnlyTheBlockTheyRead()
    {
        var (index, root) = await OpenAsync();
        await using var disposable = index;
        var firstRelease = ReleaseStateOf(index, 'T');
        var secondRelease = ReleaseStateOf(index, 'U');
        var observed = new List<(int First, int Second)>();
        index.Hooks.ReaderAdmitted = () =>
            observed.Add((firstRelease.Hooks.OutstandingBorrowCount, secondRelease.Hooks.OutstandingBorrowCount));

        _ = index.Find(ReadmePath, TestContext.CancellationToken);
        _ = index.Root(new VolumeKey('T'), TestContext.CancellationToken);
        _ = index.Search(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken);
        _ = index.Enumerate(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken).ToList();
        foreach (var row in index.EnumerateRows(new SearchQuery("readme", Subtree: root), TestContext.CancellationToken))
        {
            _ = row.Name.Length;
        }

        Assert.HasCount(5, observed);
        CollectionAssert.AreEqual(Enumerable.Repeat((1, 0), 5).ToList(), observed);
    }

    /// <summary>An unscoped query borrows every current block, so a retired block stays mapped until it ends.</summary>
    [TestMethod]
    public async Task UnscopedSearch_BorrowsEveryCurrentBlock()
    {
        var (index, _) = await OpenAsync();
        await using var disposable = index;
        var firstRelease = ReleaseStateOf(index, 'T');
        var secondRelease = ReleaseStateOf(index, 'U');
        (int First, int Second) observed = default;

        var released = await RescanWhileQueryHeldAsync(index, secondRelease,
            () => _ = index.Search(new SearchQuery("readme"), TestContext.CancellationToken), passes: 3,
            onAdmitted: () => observed = (firstRelease.Hooks.OutstandingBorrowCount,
                secondRelease.Hooks.OutstandingBorrowCount));

        Assert.AreEqual((1, 1), observed);
        Assert.IsFalse(released, "an unscoped Search must keep every block it scans mapped");
        Assert.IsTrue(CollectUntilReleased(secondRelease), "the retired block never released after the query ended");
    }
}
