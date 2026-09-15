using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#145: disposing a FileIndex releases every block mapping it holds, whether or not a
///     query has handed out a handle, so a consumer can close the .mlix at a point it chooses
///     without invoking the garbage collector.
/// </summary>
[TestClass]
public class FileIndexBlockReleaseTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    uint _volumeSerial;

    /// <summary>Creates an isolated enumeration tree and cache location.</summary>
    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    /// <summary>Removes the test tree and cached blocks.</summary>
    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    FileIndexOptions Options()
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, _volumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };
    }

    FileIndexOptions TwoDriveOptions(string secondTreeRoot, uint secondVolumeSerial)
    {
        return new FileIndexOptions
        {
            Drives = [
                new IndexedDrive('T', _treeRoot, _volumeSerial),
                new IndexedDrive('U', secondTreeRoot, secondVolumeSerial)
            ],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };
    }

    /// <summary>Disposal closes the current block even when a query handle is retained.</summary>
    [TestMethod]
    public async Task DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile()
    {
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.FindByName("readme.md").Single();
        Assert.IsFalse(entry.IsDisposed);

        await index.DisposeAsync();

        BlockFileHoldAssertions.AssertNotHeld(blockPath);
        File.Delete(blockPath);
        Assert.IsFalse(File.Exists(blockPath));
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Size);
    }

    /// <summary>Disposal releases both the retired and current snapshots after a rescan.</summary>
    [TestMethod]
    public async Task DisposeAsync_AfterARescan_ReleasesTheRetiredBlockToo()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var retiredHandle = index.FindByName("readme.md").Single();
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);
        var currentHandle = index.FindByName("second.md").Single();

        await index.DisposeAsync();

        Assert.IsTrue(retiredHandle.IsDisposed, "the retired snapshot must be released too");
        Assert.IsTrue(currentHandle.IsDisposed);
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        BlockFileHoldAssertions.AssertNotHeld(blockPath);
    }

    [TestMethod]
    public async Task DisposeAsync_AfterAGarbageCollectedRetiredSnapshot_ReleasesEveryDriveBlock()
    {
        var secondTreeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        var secondVolumeSerial = TestVolumeSerial.GetNext();
        Directory.CreateDirectory(secondTreeRoot);
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "unchanged.md"), "unchanged");
        try
        {
            var index = await OpenAndRescanWithoutRetainingThePreviousSnapshotAsync(
                secondTreeRoot, secondVolumeSerial);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            await index.DisposeAsync();

            var unchangedBlockPath = Path.Combine(_cacheDirectory,
                CacheDirectory.BlockFileName('U', secondVolumeSerial));
            BlockFileHoldAssertions.AssertNotHeld(unchangedBlockPath);
            Assert.AreEqual(0, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length);
        }
        finally
        {
            Directory.Delete(secondTreeRoot, recursive: true);
        }
    }

    /// <summary>
    ///     A rescan does not keep its retired snapshot alive. Once its handles are unreachable,
    ///     the snapshot finalizer releases the retired mapping while the index remains open.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_AfterAllRetiredHandlesAreCollected_ReleasesTheRetiredBlockWhileIndexLives()
    {
        var index = await OpenAndRescanWithoutRetainingThePreviousSnapshotAsync();
        var retiredBlockPath = Directory.EnumerateFiles(_cacheDirectory,
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
        var secondTreeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        var secondVolumeSerial = TestVolumeSerial.GetNext();
        Directory.CreateDirectory(secondTreeRoot);
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "unchanged.md"), "unchanged");
        var reachedTheGate = new ReleaseGate();
        var gate = new ReleaseGate();
        try
        {
            var index = await FileIndex.OpenAsync(TwoDriveOptions(secondTreeRoot, secondVolumeSerial),
                CancellationToken.None);
            var retiredRelease = index.CurrentSnapshot.ReleaseState;
            retiredRelease._releaseStartedForTest = () =>
            {
                reachedTheGate.Set();
                gate.Wait();
            };

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            await index.RescanAsync('T', CancellationToken.None);

            // Stands in for the snapshot finalizer: the same non-waiting release call, held at the
            // exact point the finalizer reaches once the flag is set and before a block is unmapped.
            var competingRelease = Task.Run(retiredRelease.Release);
            reachedTheGate.Wait();

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "DisposeAsync returned while a competing release had only started");

            gate.Set();
            Assert.IsTrue(await competingRelease);
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(_cacheDirectory,
                CacheDirectory.BlockFileName('U', secondVolumeSerial)));
            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(_cacheDirectory,
                CacheDirectory.BlockFileName('T', _volumeSerial)));
            Assert.AreEqual(0, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length);
        }
        finally
        {
            gate.Set();
            Directory.Delete(secondTreeRoot, recursive: true);
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
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var borrow = index.CurrentSnapshot.Borrow();
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
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        cancellation.Dispose();

        Assert.AreEqual(1, index.FindByName("readme.md", token).Count);

        // Asserted before the disposal below, which is what a stranded borrow would hang forever.
        Assert.AreEqual(0, release.OutstandingBorrowCount,
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
        var release = index.CurrentSnapshot.ReleaseState;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        await index.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(() => index.FindByName("readme.md"));
        Assert.ThrowsException<ObjectDisposedException>(() => index.FindByName("readme.md", token));

        Assert.AreEqual(0, release.OutstandingBorrowCount);
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
        index.CurrentSnapshot.ReleaseState._releaseStartedForTest =
            () => throw new InvalidOperationException("the release itself");
        using var registration = index.DisposalToken.Register(
            () => throw new InvalidOperationException("a cancellation callback of someone else's"));

        var thrown = await Assert.ThrowsExceptionAsync<AggregateException>(
            async () => await index.DisposeAsync());

        Assert.AreEqual(2, thrown.InnerExceptions.Count, "one of the two failures was dropped");
        var cancellationFailure = (AggregateException)thrown.InnerExceptions[0];
        Assert.AreEqual("a cancellation callback of someone else's",
            cancellationFailure.InnerExceptions.Single().Message,
            "the cancellation failure must come first, since it happened first");
        Assert.AreEqual("the release itself", thrown.InnerExceptions[1].Message);

        // Flattening is how a consumer gets at the leaves, the cancellation failure being the
        // AggregateException that CancelAsync itself raised. It does not preserve the order
        // above, so this asserts membership rather than position.
        var leaves = thrown.Flatten().InnerExceptions.Select(failure => failure.Message).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "a cancellation callback of someone else's", "the release itself" }, leaves);
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
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        using var registration = index.DisposalToken.Register(
            () => throw new InvalidOperationException("a cancellation callback of someone else's"));

        var thrown = await Assert.ThrowsExceptionAsync<AggregateException>(
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
        var borrow = index.CurrentSnapshot.Borrow();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            await index.RescanAsync('T', CancellationToken.None);
            Assert.AreNotSame(borrow.Snapshot, index.CurrentSnapshot,
                "the rescan did not retire the borrowed snapshot, so this proves nothing");

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "DisposeAsync returned while a reader still held a retired snapshot");

            borrow.Dispose();
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(_cacheDirectory,
                CacheDirectory.BlockFileName('T', _volumeSerial)));
            Assert.AreEqual(0, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length);
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
        var reachedTheGate = new ReleaseGate();
        var gate = new ReleaseGate();
        try
        {
            var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
            var retiredRelease = index.CurrentSnapshot.ReleaseState;
            retiredRelease._releaseStartedForTest = () =>
            {
                reachedTheGate.Set();
                gate.Wait();
            };

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            await index.RescanAsync('T', CancellationToken.None);

            var competingRelease = Task.Run(retiredRelease.Release);
            reachedTheGate.Wait();

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "third.md"), "third");
            await index.RescanAsync('T', CancellationToken.None);

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsFalse(disposal.IsCompleted,
                "a rescan pruned a retired release that had only started, so disposal had nothing to wait on");

            gate.Set();
            Assert.IsTrue(await competingRelease);
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(Path.Combine(_cacheDirectory,
                CacheDirectory.BlockFileName('T', _volumeSerial)));
            Assert.AreEqual(0, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length);
        }
        finally
        {
            gate.Set();
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
        GC.SuppressFinalize(index.CurrentSnapshot);
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    async Task<FileIndex> OpenAndRescanWithoutRetainingThePreviousSnapshotAsync()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);
        return index;
    }

    /// <summary>
    ///     A one-shot gate the release seam and the test share across threads. Deliberately not a
    ///     <see cref="ManualResetEventSlim" />: that is disposable, and a disposable captured by
    ///     the seam's closure is exactly what the quality gate refuses.
    /// </summary>
    sealed class ReleaseGate
    {
        readonly object _lock = new();
        bool _open;

        public void Set()
        {
            lock (_lock)
            {
                _open = true;
                Monitor.PulseAll(_lock);
            }
        }

        public void Wait()
        {
            lock (_lock)
            {
                while (!_open)
                {
                    Monitor.Wait(_lock);
                }
            }
        }
    }
}
