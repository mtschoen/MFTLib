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
///     <para>
///         Every test holds its reader at the snapshot's reader-admitted seam, so the reader is
///         in flight by construction when disposal begins: it has its borrow and has not read a
///         row. Nothing here depends on a scan being slow enough to still be running.
///     </para>
/// </summary>
[TestClass]
public class FileIndexDisposeCancelsQueryTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A bound on a handshake that failed, never a duration any assertion measures.</summary>
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    const uint RowCount = 64;

    /// <summary>Fewer names than rows, so the duplicate-name scan has groups to find.</summary>
    const uint DistinctNameCount = 8;

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
        var outcome = await QueryAcrossDisposalAsync((index, _) => index.DuplicateNames());

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a scan that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
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
        using var callerCancellation = new CancellationTokenSource();
        var callerToken = callerCancellation.Token;

        var outcome = await QueryAcrossDisposalAsync((index, _) => index.DuplicateNames(callerToken));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a scan carrying its caller's token must still be cancelled by disposal, not {Describe(outcome)}");
    }

    /// <summary>The same through the parallel search path, with a pattern that matches every row.</summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAParallelSearchIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync((index, _) => index.Search(new SearchQuery("file")));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a search that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    /// <summary>
    ///     A largest-file query in flight when disposal begins must be cancelled, not throw
    ///     ObjectDisposedException on guarded FileEntry property reads.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileALargestQueryIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync((index, _) => index.Largest(100));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a largest query that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    /// <summary>
    ///     A largest-file query with a subtree restriction in flight when disposal begins must
    ///     also be cancelled without throwing ObjectDisposedException on candidate navigation.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileALargestWithSubtreeFilterIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync((index, root) => index.Largest(100, under: root));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a largest query with subtree filter that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    /// <summary>
    ///     A search with a subtree filter in flight when disposal begins must observe cancellation
    ///     without throwing ObjectDisposedException.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileASearchWithSubtreeFilterIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync(
            (index, root) => index.Search(new SearchQuery("file", Under: root)));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a search with subtree filter that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    /// <summary>
    ///     A handle's children listing is a whole-drive scan too, and it holds the same borrow.
    ///     It carries no reference to the index, so disposal has no token to cancel it with and
    ///     waits it out instead: the listing answers, and only then does the block close.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAChildrenScanIsRunning_WaitsForItAndReleasesEveryBlock()
    {
        var listed = await ReadAcrossDisposalAsync((_, root) => root.Children().Count);

        Assert.AreEqual((int)RowCount, listed,
            "the listing was cut short, so disposal unmapped rows it was still walking");
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
        var outcome = await QueryAcrossDisposalAsync((index, _) =>
        {
            foreach (var unused in index.Enumerate(new SearchQuery("file")))
            {
            }
        });

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"an enumeration that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    static string Describe(Exception? outcome)
    {
        return outcome?.GetType().Name ?? "answered normally";
    }

    /// <summary>The exception a query ended with across a disposal, or null when it answered.</summary>
    Task<Exception?> QueryAcrossDisposalAsync(Action<FileIndex, FileEntry> query)
    {
        return ReadAcrossDisposalAsync((index, root) =>
        {
            try
            {
                query(index, root);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
    }

    /// <summary>
    ///     Runs one reader across a disposal, in an order nothing can reorder: the reader is held
    ///     at the seam with its borrow counted, disposal begins and delivers its cancellation,
    ///     disposal is seen still waiting on the borrow, and only then is the reader let go.
    ///     Disposal reaches its lifecycle gates after its cancellation has run every callback, a
    ///     linked query token's among them, so that seam is what says the reader has been told.
    /// </summary>
    async Task<TResult> ReadAcrossDisposalAsync<TResult>(Func<FileIndex, FileEntry, TResult> read)
    {
        var index = await FileIndex.OpenAsync(SyntheticDriveOptions(), CancellationToken.None);
        var released = new TaskCompletionSource();
        Task? disposal = null;
        try
        {
            var root = index.Root('T');
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            index.CurrentSnapshot.ReleaseState._readerAdmittedForTest = () =>
            {
                admitted.TrySetResult();
                released.Task.Wait();
            };
            index.LifecycleGatesTakenForDisposalForTest = () => cancellationDelivered.TrySetResult();

            var reader = Task.Run(() => read(index, root));
            await admitted.Task.WaitAsync(HandshakeTimeout);

            disposal = index.DisposeAsync().AsTask();
            await cancellationDelivered.Task.WaitAsync(HandshakeTimeout);
            Assert.IsFalse(disposal.IsCompleted,
                "DisposeAsync returned while the reader still held its borrow on the snapshot");

            released.TrySetResult();
            var result = await reader;
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(
                Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
            return result;
        }
        finally
        {
            // Lets a reader still parked at the seam go, so a failed handshake cannot strand its
            // borrow and leave the disposal below waiting for it.
            released.TrySetResult();
            await (disposal ?? index.DisposeAsync().AsTask());
        }
    }

    FileIndexOptions SyntheticDriveOptions()
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(SeededBlocks.Build(request, RowCount, i => $"file{i % DistinctNameCount}.dat", FixedMoment), JournalId: 7, NextUsn: 4096,
                    SkippedRecordCount: 0)))
        };
    }

}
