using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#145: disposing a FileIndex releases every block mapping it holds, whether or not a
///     query has handed out a handle, so a consumer can close the .mlix at a point it chooses
///     without invoking the garbage collector.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexBlockReleaseTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;
    string CacheDirectoryPath => _fixture.CacheDirectory;
    uint _volumeSerial;

    /// <summary>Creates an isolated enumeration tree and cache location.</summary>
    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
        _fixture = new IndexTreeFixture();
    }

    /// <summary>Removes the test tree and cached blocks.</summary>
    public void Dispose() => _fixture.Dispose();

    FileIndexOptions Options() => _fixture.Options(volumeSerial: _volumeSerial);

    FileIndexOptions TwoDriveOptions(string secondTreeRoot, uint secondVolumeSerial)
    {
        return new FileIndexOptions
        {
            Drives = [
                new IndexedDrive('T', TreeRoot, _volumeSerial, ProducerKind.Enumeration),
                new IndexedDrive('U', secondTreeRoot, secondVolumeSerial, ProducerKind.Enumeration)
            ],
            CacheDirectory = CacheDirectoryPath
        };
    }

    /// <summary>Disposal closes the current block even when a query handle is retained.</summary>
    [TestMethod]
    public async Task DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile()
    {
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken).Single();
        Assert.IsFalse(entry.IsDisposed);

        await index.DisposeAsync();

        BlockFileHoldAssertions.AssertNotHeld(blockPath);
        File.Delete(blockPath);
        Assert.IsFalse(File.Exists(blockPath));
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = entry.Size);
    }

    /// <summary>Disposal releases both the retired and current snapshots after a rescan.</summary>
    [TestMethod]
    public async Task DisposeAsync_AfterARescan_ReleasesTheRetiredBlockToo()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var retiredHandle = index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken).Single();
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);
        var currentHandle = index.Search(new SearchQuery("second.md", NameMatchMode.Exact), TestContext.CancellationToken).Single();

        await index.DisposeAsync();

        Assert.IsTrue(retiredHandle.IsDisposed, "the retired snapshot must be released too");
        Assert.IsTrue(currentHandle.IsDisposed);
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
        BlockFileHoldAssertions.AssertNotHeld(blockPath);
    }

    [TestMethod]
    public async Task DisposeAsync_AfterAGarbageCollectedRetiredSnapshot_ReleasesEveryDriveBlock()
    {
        using var secondTree = new TempDirectory("mftlib-tree");
        var secondTreeRoot = secondTree.Path;
        var secondVolumeSerial = TestVolumeSerial.GetNext();
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "unchanged.md"), "unchanged", TestContext.CancellationToken);
        var index = await OpenAndRescanWithoutRetainingThePreviousSnapshotAsync(
            secondTreeRoot, secondVolumeSerial);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        await index.DisposeAsync();

        var unchangedBlockPath = Path.Combine(CacheDirectoryPath,
            CacheDirectory.BlockFileName('U', secondVolumeSerial));
        BlockFileHoldAssertions.AssertNotHeld(unchangedBlockPath);
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.retired-*"));
    }

    /// <summary>
    ///     MFTLib#584: a retained handle keeps only its own drive's block mapped, so rescanning
    ///     another drive releases that drive's retired block while the handle stays readable.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OfAnotherDrive_ReleasesItsRetiredBlockWhileAHandleIsHeld()
    {
        using var secondTree = new TempDirectory("mftlib-tree");
        var secondTreeRoot = secondTree.Path;
        var secondVolumeSerial = TestVolumeSerial.GetNext();
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "unchanged.md"), "unchanged", TestContext.CancellationToken);
        var index = await FileIndex.OpenAsync(TwoDriveOptions(secondTreeRoot, secondVolumeSerial),
            CancellationToken.None);
        var held = index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken).Single();
        var retiredRelease = ReleaseStateOf(index, 'U');
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['U'], CancellationToken.None);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.IsTrue(retiredRelease.IsReleaseComplete, "a handle on drive T kept drive U's retired block mapped");
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath,
            CacheDirectory.BlockFileName('U', secondVolumeSerial) + ".retired-*"));
        Assert.AreEqual("readme.md", held.Name);
        await index.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static DriveBlockRelease ReleaseStateOf(FileIndex index, char drive) =>
        LookupEngine.FindDriveBlock(index.CurrentSnapshot, drive)!.ReleaseState;

    /// <summary>
    ///     A rescan does not keep its retired snapshot alive. Once its handles are unreachable,
    ///     the snapshot finalizer releases the retired mapping while the index remains open.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_AfterAllRetiredHandlesAreCollected_ReleasesTheRetiredBlockWhileIndexLives()
    {
        var index = await OpenAndRescanWithoutRetainingThePreviousSnapshotAsync();
        var retiredBlockPath = Directory.EnumerateFiles(CacheDirectoryPath,
            CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*").Single();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        BlockFileHoldAssertions.AssertNotHeld(retiredBlockPath);
        Assert.IsFalse(File.Exists(retiredBlockPath));

        await index.DisposeAsync();
    }

    /// <summary>
    ///     A release that has started but not finished is not a released snapshot. Disposal that
    ///     arrives while the snapshot finalizer is partway through the retired snapshot's blocks
    ///     must wait for that release to finish, not return on the flag that marks it begun,
    ///     because returning leaves the retired mapping and its shared block open after the
    ///     consumer was told the index is closed.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileACompetingReleaseIsInProgress_WaitsForThatReleaseToFinish()
    {
        using var secondTree = new TempDirectory("mftlib-tree");
        var secondTreeRoot = secondTree.Path;
        var secondVolumeSerial = TestVolumeSerial.GetNext();
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "unchanged.md"), "unchanged", TestContext.CancellationToken);
        var reachedTheGate = new TestGate();
        var gate = new TestGate();
        try
        {
            var index = await FileIndex.OpenAsync(TwoDriveOptions(secondTreeRoot, secondVolumeSerial),
                CancellationToken.None);
            var retiredBlock = LookupEngine.FindDriveBlock(index.CurrentSnapshot, 'T')!;
            var retiredRelease = retiredBlock.ReleaseState;
            retiredRelease.Hooks._releaseStarted = () =>
            {
                reachedTheGate.MarkEntered();
                gate.WaitForRelease();
            };

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(['T'], CancellationToken.None);

            // Stands in for the snapshot finalizer: the same non-waiting release call, held at the
            // exact point the finalizer reaches once the flag is set and before a block is unmapped.
            var competingRelease = Task.Run(retiredRelease.ReleaseUnborrowed);
            TestGate.WaitSynchronously(reachedTheGate.Entered);

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "DisposeAsync returned while a competing release had only started");

            gate.Release();
            Assert.IsTrue(await competingRelease);
            await disposal;
            GC.KeepAlive(retiredBlock);

            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(CacheDirectoryPath,
                CacheDirectory.BlockFileName('U', secondVolumeSerial)));
            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(CacheDirectoryPath,
                CacheDirectory.BlockFileName('T', _volumeSerial)));
            Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.retired-*"));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     A borrow is the reader gate a query holds for its whole duration. Disposal waits for it
    ///     to come back before it unmaps anything, so the mapping a scan is reading stays valid
    ///     until that scan has left it.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileABorrowIsHeld_WaitsForItAndThenReleasesTheBlockFile()
    {
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var borrow = new SnapshotBorrow(index.CurrentSnapshot);
        try
        {
            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "DisposeAsync returned while a reader still held the snapshot");

            borrow.Dispose();
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(blockPath);
        }
        finally
        {
            borrow.Dispose();
            await index.DisposeAsync();
        }
    }

    /// <summary>
    ///     A caller whose own token source was disposed without being cancelled must not be able
    ///     to wedge the index. On this runtime such a token behaves like a live uncancelled one
    ///     and the query answers normally; a runtime that refused to link it would make the query
    ///     throw instead. Either way the borrow is accounted for, which is what disposal depends
    ///     on: a borrow counted and then stranded by a throw would make every later release wait
    ///     for a reader that does not exist.
    /// </summary>
    [TestMethod]
    public async Task AQueryWhoseTokenSourceWasDisposed_LeavesNoBorrowOutstanding()
    {
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var release = index.CurrentSnapshot[0].ReleaseState;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        cancellation.Dispose();

        Assert.HasCount(1, index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), token));

        // Asserted before the disposal below, which is what a stranded borrow would hang forever.
        Assert.AreEqual(0, release.Hooks.OutstandingBorrowCount,
            "the query kept the borrow it took, so nothing can ever release this snapshot");

        await index.DisposeAsync();

        BlockFileHoldAssertions.AssertNotHeld(blockPath);
    }

    /// <summary>
    ///     A query started after disposal is refused before it counts anything, so the refusal
    ///     path leaves no borrow behind either. Asserted for both shapes of query token: a caller
    ///     that passes none observes the disposal signal directly, and a caller that passes one
    ///     gets a linked source that the refusal has to give back rather than strand.
    /// </summary>
    [TestMethod]
    public async Task AQueryStartedAfterDisposal_ThrowsAndLeavesNoBorrowOutstanding()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var release = index.CurrentSnapshot[0].ReleaseState;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        await index.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken));
        Assert.ThrowsExactly<ObjectDisposedException>(() => index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), token));

        Assert.AreEqual(0, release.Hooks.OutstandingBorrowCount);
    }

    /// <summary>
    ///     When both halves of a disposal fail, neither failure disappears. The cancellation
    ///     failure is captured so the release can still run, and a release that then fails too
    ///     would otherwise carry the only exception out: the captured one has nowhere left to go,
    ///     and a consumer told the release failed would never learn that its own callback threw
    ///     first. Both come out together instead, cancellation first because it happened first.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhenBothTheCancellationAndTheReleaseFail_ReportsBoth()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        index.CurrentSnapshot[0].ReleaseState.Hooks._releaseStarted =
            () => throw new InvalidOperationException("the release itself");
        await using var registration = index.DisposalToken.Register(
            () => throw new InvalidOperationException("a cancellation callback of someone else's"));

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            async () => await index.DisposeAsync());

        Assert.HasCount(2, thrown.InnerExceptions, "one of the two failures was dropped");
        var cancellationFailure = (AggregateException)thrown.InnerExceptions[0];
        Assert.AreEqual("a cancellation callback of someone else's",
            cancellationFailure.InnerExceptions.Single().Message,
            "the cancellation failure must come first, since it happened first");
        Assert.AreEqual("the release itself", thrown.InnerExceptions[1].Message);

        // Flattening is how a consumer gets at the leaves, the cancellation failure being the
        // AggregateException that CancelAsync itself raised. It does not preserve the order
        // above, so this asserts membership rather than position.
        var leaves = thrown.Flatten().InnerExceptions.Select(failure => failure.Message).ToArray();
        Assert.AreSequenceEqual(
            ["a cancellation callback of someone else's", "the release itself"], leaves, SequenceOrder.InAnyOrder);
    }

    /// <summary>
    ///     Cancelling the queries in flight is a step on the way to unmapping, not a reason to
    ///     stop: a callback registered on the disposal signal belongs to someone else, and a
    ///     throw from it must not leave the blocks mapped with the disposed flag already set,
    ///     which is a state no later call can recover from because disposal returns early once
    ///     that flag is set. The failure is still raised, after the mappings are gone.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhenACancellationCallbackThrows_StillReleasesTheBlock()
    {
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await using var registration = index.DisposalToken.Register(
            () => throw new InvalidOperationException("a cancellation callback of someone else's"));

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            async () => await index.DisposeAsync());

        Assert.IsInstanceOfType<InvalidOperationException>(thrown.InnerExceptions.Single());
        BlockFileHoldAssertions.AssertNotHeld(blockPath);
    }

    /// <summary>
    ///     A query that began before a rescan holds a borrow on the snapshot that rescan retires.
    ///     Disposal waits for that borrow too, which is why the count lives on the release state
    ///     rather than on the index's current snapshot.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileABorrowOnARetiredSnapshotIsHeld_WaitsForIt()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var borrow = new SnapshotBorrow(index.CurrentSnapshot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(['T'], CancellationToken.None);
            Assert.AreNotSame(borrow.DriveBlocks, index.CurrentSnapshot,
                "the rescan did not retire the borrowed snapshot, so this proves nothing");

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "DisposeAsync returned while a reader still held a retired snapshot");

            borrow.Dispose();
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(CacheDirectoryPath,
                CacheDirectory.BlockFileName('T', _volumeSerial)));
            Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.retired-*"));
        }
        finally
        {
            borrow.Dispose();
            await index.DisposeAsync();
        }
    }

    /// <summary>
    ///     Publication drops the retained release state of retired snapshots that are done with
    ///     their blocks. A release that has merely started is not done, so a second rescan must
    ///     keep that record: dropping it is what leaves a later disposal with nothing to wait on.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_WhileARetiredReleaseIsInProgress_KeepsItForDisposalToWaitOn()
    {
        var reachedTheGate = new TestGate();
        var gate = new TestGate();
        try
        {
            var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
            var retiredBlock = LookupEngine.FindDriveBlock(index.CurrentSnapshot, 'T')!;
            var retiredRelease = retiredBlock.ReleaseState;
            retiredRelease.Hooks._releaseStarted = () =>
            {
                reachedTheGate.MarkEntered();
                gate.WaitForRelease();
            };

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(['T'], CancellationToken.None);

            var competingRelease = Task.Run(retiredRelease.ReleaseUnborrowed);
            TestGate.WaitSynchronously(reachedTheGate.Entered);

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "third.md"), "third", TestContext.CancellationToken);
            await index.RescanAsync(['T'], CancellationToken.None);

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "a rescan pruned a retired release that had only started, so disposal had nothing to wait on");

            gate.Release();
            Assert.IsTrue(await competingRelease);
            await disposal;
            GC.KeepAlive(retiredBlock);

            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(CacheDirectoryPath,
                CacheDirectory.BlockFileName('T', _volumeSerial)));
            Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.retired-*"));
        }
        finally
        {
            gate.Release();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Design", "CA1816",
        Justification = "The regression intentionally prevents finalization so disposal must release a retired snapshot.")]
    async Task<FileIndex> OpenAndRescanWithoutRetainingThePreviousSnapshotAsync(
        string secondTreeRoot, uint secondVolumeSerial)
    {
        var index = await FileIndex.OpenAsync(TwoDriveOptions(secondTreeRoot, secondVolumeSerial),
            CancellationToken.None);
        GC.SuppressFinalize(LookupEngine.FindDriveBlock(index.CurrentSnapshot, 'T')!);
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second",
            TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    async Task<FileIndex> OpenAndRescanWithoutRetainingThePreviousSnapshotAsync()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second",
            TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);
        return index;
    }

    public TestContext TestContext { get; set; } = null!;
}
