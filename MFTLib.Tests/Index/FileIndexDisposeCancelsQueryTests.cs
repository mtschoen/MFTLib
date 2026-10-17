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
public sealed class FileIndexDisposeCancelsQueryTests : IDisposable
{
    static readonly DateTime FixedMoment = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    const uint RowCount = 64;

    /// <summary>Fewer names than rows, so the duplicate-name scan has groups to find.</summary>
    const uint DistinctNameCount = 8;

    IndexTreeFixture _fixture = null!;
    string _cacheDirectory => _fixture.CacheDirectory;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture(seedDocuments: false);

    public void Dispose() => _fixture.Dispose();

    /// <summary>The same through the parallel search path, with a pattern that matches every row.</summary>
    [TestMethod]
    public async Task DisposeAsync_WhileAParallelSearchIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync((index, _) => index.Search(new SearchQuery("file")));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a search that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    /// <summary>
    ///     A search with a subtree filter in flight when disposal begins must observe cancellation
    ///     without throwing ObjectDisposedException.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WhileASearchWithSubtreeFilterIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync(
            (index, root) => index.Search(new SearchQuery("file", Subtree: root)));

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

    [TestMethod]
    public async Task DisposeAsync_WhileEnumerateRowsIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync((index, _) => ConsumeRows(index, new SearchQuery(null)));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a row enumeration that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    [TestMethod]
    public async Task DisposeAsync_WhileEnumerateRowsHasLiveCallerToken_CancelsItAndReleasesEveryBlock()
    {
        using var source = new CancellationTokenSource();
        var outcome = await QueryAcrossDisposalAsync(
            (index, _) => ConsumeRows(index, new SearchQuery(null), source.Token));

        Assert.IsFalse(source.IsCancellationRequested, "Only index disposal cancels this enumeration.");
        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a row enumeration with a live caller token must be cancelled by disposal, not {Describe(outcome)}");
    }

    [TestMethod]
    public async Task DisposeAsync_WhileEnumerateRowsWithSubtreeFilterIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync(
            (index, root) => ConsumeRows(index, new SearchQuery(null, Subtree: root)));

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"a row enumeration with subtree filter must be cancelled by disposal, not {Describe(outcome)}");
    }

    static void ConsumeRows(FileIndex index, SearchQuery query, CancellationToken cancellationToken = default)
    {
        foreach (var row in index.EnumerateRows(query, cancellationToken))
        {
            _ = row.Size;
        }
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
            Action holdTheReader = () =>
            {
                admitted.TrySetResult();
                released.Task.Wait();
            };
            index.Hooks.ReaderAdmitted = holdTheReader;
            index.CurrentSnapshot[0].ReleaseState.Hooks.ReaderAdmitted = holdTheReader;
            index.Hooks.LifecycleGatesTakenForDisposal = () => cancellationDelivered.TrySetResult();

            var reader = Task.Run(() => read(index, root));
            await admitted.Task.WaitAsync(HangGuard * 3);

            disposal = index.DisposeAsync().AsTask();
            await cancellationDelivered.Task.WaitAsync(HangGuard * 3);
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

    FileIndexOptions SyntheticDriveOptions() =>
        _fixture.Options(producer: ProducerKind.Mft) with
        {
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(SeededBlocks.Build(request, RowCount, i => $"file{i % DistinctNameCount}.dat", FixedMoment),
                    SkippedRecordCount: 0)))
        };

}
