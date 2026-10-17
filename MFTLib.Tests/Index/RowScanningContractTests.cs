using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Data-driven lifetime and cancellation contracts across all six public query operations:
///     borrow release, admission cancellation, 4096-row periodic bounds, and owner disposal.
/// </summary>
[TestClass]
public sealed class RowScanningContractTests : IDisposable
{
    static readonly DateTime FixedMoment = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    IndexTreeFixture _fixture = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture(seedDocuments: false);

    public void Dispose() => _fixture.Dispose();

    public delegate int QueryCall(FileIndex index, FileEntry root, string path,
        SearchQuery query, CancellationToken cancellationToken);

    public static IEnumerable<object[]> Queries()
    {
        yield return ["Find", (QueryCall)((index, _, path, _, token) =>
            index.Find(path, token).HasValue ? 1 : 0), 1];
        yield return ["Search", (QueryCall)((index, _, _, query, token) =>
            index.Search(query, token).Count), 128];
        yield return ["Enumerate", (QueryCall)((index, _, _, query, token) =>
            index.Enumerate(query, token).Count()), 128];
        yield return ["EnumerateRows", (QueryCall)((index, _, _, query, token) =>
            CountRows(index, query, token)), 128];
        yield return ["Root", (QueryCall)((index, _, _, _, token) =>
            index.Root('T', token).IsValid ? 1 : 0), 1];
        yield return ["Children", (QueryCall)((_, root, _, _, token) =>
            root.Children(token).Count), 64];
    }

    static int CountRows(FileIndex index, SearchQuery query, CancellationToken token)
    {
        var count = 0;
        foreach (var row in index.EnumerateRows(query, token))
        {
            _ = row.Size;
            count++;
        }

        return count;
    }

    static void SetAdmission(FileIndex index, Action? admitted)
    {
        index.Hooks.ReaderAdmitted = admitted;
        foreach (var block in index.CurrentSnapshot)
        {
            block.ReleaseState.Hooks.ReaderAdmitted = admitted;
        }
    }

    Task<FileIndex> OpenSyntheticIndexAsync(uint fileCount = 64)
    {
        var firstRoot = Path.Combine(_fixture.TreeRoot, "first");
        var secondRoot = Path.Combine(_fixture.TreeRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        var options = new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('T', firstRoot, 1),
                new IndexedDrive('U', secondRoot, 2)
            ],
            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(
                    SeededBlocks.Build(request, fileCount, row => $"file{row}.dat", FixedMoment),
                    SkippedRecordCount: 0)))
        };

        return FileIndex.OpenAsync(options, CancellationToken.None);
    }

    [TestMethod]
    [DynamicData(nameof(Queries))]
    public async Task LiveToken_CompletesAndReturnsEveryBorrow(string api, QueryCall call, int expectedCount)
    {
        await using var index = await OpenSyntheticIndexAsync();
        var root = index.Root('T');
        var path = Path.Combine(root.Path, "file6.dat");
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();

        using var cancellation = new CancellationTokenSource();
        int[]? admittedCounts = null;
        try
        {
            SetAdmission(index, () =>
            {
                admittedCounts ??= blocks.Select(block => block.ReleaseState.Hooks.OutstandingBorrowCount).ToArray();
            });

            var count = call(index, root, path, new SearchQuery("file"), cancellation.Token);
            Assert.AreEqual(expectedCount, count, api);
        }
        finally
        {
            SetAdmission(index, null);
        }

        CollectionAssert.AreEqual(api == "Children" ? new[] { 1, 0 } : new[] { 1, 1 }, admittedCounts, api);
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Queries))]
    public async Task CancelledBeforeAdmission_TakesNoBorrow(string api, QueryCall call, int expectedCount)
    {
        await using var index = await OpenSyntheticIndexAsync();
        var root = index.Root('T');
        var path = Path.Combine(root.Path, "file6.dat");
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var admittedCalled = false;
        OperationCanceledException? thrown = null;
        try
        {
            SetAdmission(index, () => admittedCalled = true);
            try
            {
                call(index, root, path, new SearchQuery("file"), token);
            }
            catch (OperationCanceledException exception)
            {
                thrown = exception;
            }
        }
        finally
        {
            SetAdmission(index, null);
        }

        Assert.IsNotNull(thrown, $"{api} did not throw OperationCanceledException.");
        Assert.IsFalse(admittedCalled, $"{api} invoked admission hook when pre-cancelled.");
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Queries))]
    public async Task CancelledAfterAdmission_StopsBeforeFirstRow(string api, QueryCall call, int expectedCount)
    {
        await using var index = await OpenSyntheticIndexAsync();
        var root = index.Root('T');
        var path = Path.Combine(root.Path, "file6.dat");
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();

        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        int[]? admittedCounts = null;
        OperationCanceledException? thrown = null;
        try
        {
            SetAdmission(index, () =>
            {
                if (admittedCounts is null)
                {
                    admittedCounts = blocks.Select(block => block.ReleaseState.Hooks.OutstandingBorrowCount).ToArray();
                    cancellation.Cancel();
                }
            });

            try
            {
                call(index, root, path, new SearchQuery("file"), token);
            }
            catch (OperationCanceledException exception)
            {
                thrown = exception;
            }
        }
        finally
        {
            SetAdmission(index, null);
        }

        Assert.IsNotNull(thrown, $"{api} did not throw OperationCanceledException.");
        CollectionAssert.AreEqual(api == "Children" ? new[] { 1, 0 } : new[] { 1, 1 }, admittedCounts, api);
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Queries))]
    public async Task DisposedOwner_RejectsTheQuery(string api, QueryCall call, int expectedCount)
    {
        var index = await OpenSyntheticIndexAsync();
        var root = index.Root('T');
        var path = Path.Combine(root.Path, "file6.dat");
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();

        await index.DisposeAsync();

        ObjectDisposedException? thrown = null;
        try
        {
            call(index, root, path, new SearchQuery("file"), CancellationToken.None);
        }
        catch (ObjectDisposedException exception)
        {
            thrown = exception;
        }

        Assert.IsNotNull(thrown, $"{api} did not throw ObjectDisposedException.");
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Queries))]
    public async Task Cancellation_RespectsTheRowBoundAndReturnsEveryBorrow(string api, QueryCall call, int expectedCount)
    {
        await using var index = await OpenSyntheticIndexAsync(16_384);
        var root = index.Root('T');
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();
        foreach (var block in blocks)
        {
            Assert.AreEqual(16_390u, block.Block.Header.RowCount);
            Assert.IsTrue(block.Block.Header.RowCount < ScanPartitioning.SingleThreadedRowThreshold);
        }

        if (api == "Root")
        {
            var checkpoints = new List<uint>();
            using var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            Action<uint> observe = checkpoints.Add;
            foreach (var block in blocks)
            {
                block.Block.Hooks.RowScanCheckpoint = observe;
            }

            OperationCanceledException? thrown = null;
            try
            {
                SetAdmission(index, () => cancellation.Cancel());
                try
                {
                    call(index, root, Path.Combine(root.Path, "missing-child.dat"), new SearchQuery("missing"), token);
                }
                catch (OperationCanceledException exception)
                {
                    thrown = exception;
                }
            }
            finally
            {
                SetAdmission(index, null);
                foreach (var block in blocks)
                {
                    block.Block.Hooks.RowScanCheckpoint = null;
                }
            }

            Assert.IsNotNull(thrown, "Root did not throw OperationCanceledException.");
            Assert.AreEqual(0, checkpoints.Count, "Root directly reads its root row and has no row scan checkpoints.");
            foreach (var block in blocks)
            {
                Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
            }

            return;
        }

        uint[] cancellationPoints = [4096u, 8192u];
        if (api is "Search" or "Enumerate" or "EnumerateRows")
        {
            SearchQuery[] testQueries =
            [
                new("missing"),
                new(null, IsDirectory: true),
                new("missing", Subtree: root)
            ];
            foreach (var query in testQueries)
            {
                foreach (var cancelAt in cancellationPoints)
                {
                    AssertCheckpointCancellation(api, call, index, root, query, cancelAt);
                }
            }
        }
        else
        {
            var query = new SearchQuery("missing");
            foreach (var cancelAt in cancellationPoints)
            {
                AssertCheckpointCancellation(api, call, index, root, query, cancelAt);
            }
        }
    }

    static void AssertCheckpointCancellation(string api, QueryCall call, FileIndex index,
        FileEntry root, SearchQuery query, uint cancelAt)
    {
        var blocks = index.CurrentSnapshot.ToArray();
        var checkpoints = new List<uint>();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Action<uint> observe = row =>
        {
            checkpoints.Add(row);
            if (row == cancelAt)
            {
                cancellation.Cancel();
            }
        };
        foreach (var block in blocks)
        {
            block.Block.Hooks.RowScanCheckpoint = observe;
        }

        OperationCanceledException? failure = null;
        try
        {
            call(index, root, Path.Combine(root.Path, "missing-child.dat"), query, token);
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
        }
        finally
        {
            foreach (var block in blocks)
            {
                block.Block.Hooks.RowScanCheckpoint = null;
            }
        }

        Assert.IsNotNull(failure, $"{api} completed after cancellation at row {cancelAt}.");
        CollectionAssert.AreEqual(cancelAt == 4096u
            ? new uint[] { 0, 4096 }
            : new uint[] { 0, 4096, 8192 }, checkpoints.ToArray(), api);
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
        }
    }
}
