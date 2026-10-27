using MFTLib.Index;
using MFTLib.Tests.TestSupport;
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
public sealed class FileIndexDisposeCancelsQueryTests : IndexTreeTestBase
{
    static readonly DateTime FixedMoment = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    const uint RowCount = 64;

    /// <summary>Fewer names than rows, so the duplicate-name scan has groups to find.</summary>
    const uint DistinctNameCount = 8;

    protected override bool SeedDocuments => false;

    /// <summary>The same through the parallel search path, with a pattern that matches every row.</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposeAsync_WhileAParallelSearchIsRunning_CancelsItAndReleasesEveryBlock(bool cancelWhileAdmitted = false)
    {
        var outcome = await QueryAcrossDisposalAsync(
            (index, _, cancellationToken) => index.Search(new SearchQuery("file"), cancellationToken),
            cancelWhileAdmitted);

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
            (index, root) => index.Search(new SearchQuery("file", Subtree: root), TestContext.CancellationToken));

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
        var listed = await ReadAcrossDisposalAsync((_, root) => root.Children(TestContext.CancellationToken).Count);

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
            foreach (var unused in index.Enumerate(new SearchQuery("file"), TestContext.CancellationToken))
            {
            }
        });

        Assert.IsInstanceOfType<OperationCanceledException>(outcome,
            $"an enumeration that held the snapshot when disposal began must be cancelled, not {Describe(outcome)}");
    }

    [TestMethod]
    public async Task DisposeAsync_WhileEnumerateRowsIsRunning_CancelsItAndReleasesEveryBlock()
    {
        var outcome = await QueryAcrossDisposalAsync((index, _) => ConsumeRows(index, new SearchQuery(null), TestContext.CancellationToken));

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
            (index, root) => ConsumeRows(index, new SearchQuery(null, Subtree: root), TestContext.CancellationToken));

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
    Task<Exception?> QueryAcrossDisposalAsync(Action<FileIndex, FileEntry, CancellationToken> query, bool cancelWhileAdmitted = false)
    {
        return ReadAcrossDisposalAsync((index, root, cancellationToken) =>
        {
            try
            {
                query(index, root, cancellationToken);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }, cancelWhileAdmitted);
    }

    Task<Exception?> QueryAcrossDisposalAsync(Action<FileIndex, FileEntry> query, bool cancelWhileAdmitted = false) =>
        QueryAcrossDisposalAsync((index, root, _) => query(index, root), cancelWhileAdmitted);

    /// <summary>
    ///     Runs one reader across a disposal, in an order nothing can reorder: the reader is held
    ///     at the seam with its borrow counted, disposal begins and delivers its cancellation,
    ///     disposal is seen still waiting on the borrow, and only then is the reader let go.
    ///     Disposal reaches its lifecycle gates after its cancellation has run every callback, a
    ///     linked query token's among them, so that seam is what says the reader has been told.
    /// </summary>
    async Task<TResult> ReadAcrossDisposalAsync<TResult>(
        Func<FileIndex, FileEntry, CancellationToken, TResult> read,
        bool cancelWhileAdmitted = false)
    {
        var index = await FileIndex.OpenAsync(SyntheticDriveOptions(), CancellationToken.None);
        using var runnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var token = runnerCancellation.Token;
        var released = new TaskCompletionSource();
        Task? disposal = null;
        Task<TResult>? reader = null;
        var initialBlock = index.CurrentSnapshot.Count > 0 ? index.CurrentSnapshot[0] : null;
        try
        {
            var root = index.Root('T', token);
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holdTheReader = () =>
            {
                admitted.TrySetResult();
                released.Task.Wait(TestContext.CancellationToken);
            };
            index.Hooks.ReaderAdmitted = holdTheReader;
            initialBlock?.ReleaseState.Hooks._readerAdmitted = holdTheReader;
            index.Hooks.LifecycleGatesTakenForDisposal = () => cancellationDelivered.TrySetResult();

            reader = Task.Run(() => read(index, root, token), TestContext.CancellationToken);
            await admitted.Task.WaitAsync(HangGuard * 3, token);

            if (cancelWhileAdmitted)
            {
                await runnerCancellation.CancelAsync();
            }

            disposal = index.DisposeAsync().AsTask();
            if (!cancelWhileAdmitted)
            {
                await cancellationDelivered.Task.WaitAsync(HangGuard * 3, token);
                Assert.IsFalse(disposal.IsCompleted,
                    "DisposeAsync returned while the reader still held its borrow on the snapshot");
            }

            released.TrySetResult();
            var result = await reader;
            await disposal;

            BlockFileHoldAssertions.AssertNotHeld(
                Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', 0x0BADF00D)));
            return result;
        }
        finally
        {
            // Lets a reader still parked at the seam go, so a failed handshake cannot strand its
            // borrow and leave the disposal below waiting for it.
            released.TrySetResult();
            index.Hooks.ReaderAdmitted = null;
            initialBlock?.ReleaseState.Hooks._readerAdmitted = null;

            if (reader != null)
            {
                try
                {
                    await reader;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await (disposal ?? index.DisposeAsync().AsTask());
        }
    }

    Task<TResult> ReadAcrossDisposalAsync<TResult>(Func<FileIndex, FileEntry, TResult> read) =>
        ReadAcrossDisposalAsync((index, root, _) => read(index, root));

    FileIndexOptions SyntheticDriveOptions() =>
        Fixture.Options(producer: ProducerKind.Mft) with
        {
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(SeededBlocks.Build(request, RowCount, i => $"file{i % DistinctNameCount}.dat", FixedMoment),
                    SkippedRecordCount: 0)))
        };

    public TestContext TestContext { get; set; } = null!;
}
