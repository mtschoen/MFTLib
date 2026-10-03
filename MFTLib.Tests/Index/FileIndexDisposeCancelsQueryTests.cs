using System.Diagnostics;
using MFTLib.Tests.TestSupport;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The crash this closes: a duplicate-name scan on a worker thread while the owning thread
///     disposed the index, which unmapped the block the scan was reading and killed the process
///     with an access violation. Disposal now cancels the queries in flight and waits for them to
///     leave before it unmaps, so the scan ends with an exception its caller can handle and the
///     process survives. That the test completes at all is the second half of the assertion: an
///     access violation takes the whole test host with it.
/// </summary>
[TestClass]
public class FileIndexDisposeCancelsQueryTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Rows enough to exercise every row-scanning query path.</summary>
    const uint RowCount = 600_000;

    const uint DistinctNameCount = 1000;

    OwnedIndexDirectories _directories = null!;
    string _cacheDirectory = null!;
    string _treeRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(_treeRoot);
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    [TestMethod]
    public async Task DisposeAsync_WhileADuplicateNameScanIsRunning_CancelsItAndCompletes()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var scan = Task.Run(() =>
        {
            try
            {
                index.DuplicateNames();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        // Waits for the borrow itself, not for a worker that is merely about to take one. Until
        // the count moves, disposal could win the race and the scan would be turned away before
        // it ever read a row, which proves nothing about what happens to a scan in flight.
        WaitUntilBorrowTaken(release, "the scan");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a scan that held the snapshot when disposal began must be cancelled, not {outcome?.GetType().Name ?? "answered normally"}");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    /// <summary>
    ///     The same for a caller that passed a token of its own. A query with no token observes
    ///     the index's disposal signal directly, and one with a token observes a source linking
    ///     the two; this is the second of those paths, and linking must not cost the query its
    ///     link to disposal.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAScanWithACallerTokenIsRunning_CancelsItToo()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        using var callerCancellation = new CancellationTokenSource();
        var callerToken = callerCancellation.Token;
        var scan = Task.Run(() =>
        {
            try
            {
                index.DuplicateNames(callerToken);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        WaitUntilBorrowTaken(release, "the scan");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a scan carrying its caller's token must still be cancelled by disposal, not {outcome?.GetType().Name ?? "answered normally"}");
    }

    /// <summary>
    ///     The same race through the parallel search path, and the other half of the contract:
    ///     once disposal returns, the block file is closed, so a reader it cancelled is not still
    ///     holding a mapping open behind it.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAParallelSearchIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var scan = Task.Run(() =>
        {
            try
            {
                // Matches every row, so the search materializes the whole drive and is still
                // running when the handshake below sees its borrow. A pattern that matched one
                // row would be over in a few milliseconds and the race would not happen.
                index.Search(new SearchQuery("file"));
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        WaitUntilBorrowTaken(release, "the search");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a search that held the snapshot when disposal began must be cancelled, not {outcome?.GetType().Name ?? "answered normally"}");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    /// <summary>
    ///     A largest-file query in flight when disposal begins must be cancelled promptly, not
    ///     throw ObjectDisposedException on guarded FileEntry property reads, and allow deterministic
    ///     release to unmap every block once the borrow is returned.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileALargestQueryIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var scan = Task.Run(() =>
        {
            try
            {
                index.Largest(100);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        WaitUntilBorrowTaken(release, "the largest query");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a largest query that held the snapshot when disposal began must be cancelled, not {outcome?.GetType().Name ?? "answered normally"}");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    /// <summary>
    ///     A largest-file query with a subtree restriction in flight when disposal begins must
    ///     also be cancelled without throwing ObjectDisposedException on candidate navigation.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileALargestWithSubtreeFilterIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var root = index.Root('T');
        var scan = Task.Run(() =>
        {
            try
            {
                index.Largest(100, under: root);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        WaitUntilBorrowTaken(release, "the largest query with subtree filter");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a largest query with subtree filter that held the snapshot when disposal began must be cancelled, not {outcome?.GetType().Name ?? "answered normally"}");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    /// <summary>
    ///     A search with a subtree filter in flight when disposal begins must observe cancellation
    ///     during post-scan subtree filtering without throwing ObjectDisposedException.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileASearchWithSubtreeFilterIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var root = index.Root('T');
        var scan = Task.Run(() =>
        {
            try
            {
                index.Search(new SearchQuery("file", Under: root));
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        WaitUntilBorrowTaken(release, "the search with subtree filter");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a search with subtree filter that held the snapshot when disposal began must be cancelled, not {outcome?.GetType().Name ?? "answered normally"}");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    /// <summary>
    ///     A handle's children listing is a whole-drive scan too, and it holds the same borrow.
    ///     It carries no reference to the index, so disposal has no token to cancel it with and
    ///     waits it out instead: the listing answers, and only then does the block close.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAChildrenScanIsRunning_WaitsForItAndReleasesEveryBlock()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var root = index.Root('T');
        var listing = Task.Run(() => root.Children().Count);

        WaitUntilBorrowTaken(release, "the listing");

        await index.DisposeAsync();

        Assert.AreEqual((int)RowCount, await listing,
            "the listing was cut short, so disposal unmapped rows it was still walking");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    /// <summary>
    ///     A streaming enumeration in flight when disposal begins holds the same borrow and
    ///     observes the same disposal-linked token as every other query on the index, so
    ///     disposal cancels it rather than waiting out a whole-drive pass, and the block
    ///     closes once the enumerator's borrow is returned.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAnEnumerationIsInFlight_CancelsItAndReleasesEveryBlock()
    {
        var index = await FileIndex.OpenAsync(LargeSyntheticDriveOptions(), CancellationToken.None);
        var release = index.CurrentSnapshot.ReleaseState;
        var scan = Task.Run(() =>
        {
            try
            {
                // Matches every file row, so the enumeration is still streaming when the
                // handshake below sees its borrow.
                foreach (var _ in index.Enumerate(new SearchQuery("file")))
                {
                }

                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        WaitUntilBorrowTaken(release, "the enumeration");

        await index.DisposeAsync();
        var outcome = await scan;

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"an enumeration that held the snapshot when disposal began must be cancelled, not {outcome?.GetType().Name ?? "answered normally"}");
        BlockFileHoldAssertions.AssertNotHeld(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
    }

    static void WaitUntilBorrowTaken(SnapshotRelease release, string reader)
    {
        var elapsed = Stopwatch.StartNew();
        while (release.OutstandingBorrowCount == 0)
        {
            if (elapsed.Elapsed > HandshakeTimeout)
            {
                Assert.Fail($"{reader} never took its borrow, so it was never in flight when disposal began");
            }

            Thread.SpinWait(20);
        }
    }

    FileIndexOptions LargeSyntheticDriveOptions()
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, _) => Task.FromResult(
                new MftBlockProduceResult(MftBlockFixture.Build(request, RowCount, i => $"file{i % DistinctNameCount}.dat", FixedMoment), JournalId: 7, NextUsn: 4096,
                    SkippedRecordCount: 0))
        };
    }

}
