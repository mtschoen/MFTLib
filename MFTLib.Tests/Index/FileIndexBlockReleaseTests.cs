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
