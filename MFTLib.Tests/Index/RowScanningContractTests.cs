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
        Action cancel = cancellation.Cancel;
        int[]? admittedCounts = null;
        OperationCanceledException? thrown = null;
        try
        {
            SetAdmission(index, () =>
            {
                if (admittedCounts is null)
                {
                    admittedCounts = blocks.Select(block => block.ReleaseState.Hooks.OutstandingBorrowCount).ToArray();
                    cancel();
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
            Action cancel = cancellation.Cancel;
            Action<uint> observe = checkpoints.Add;
            foreach (var block in blocks)
            {
                block.Block.Hooks.RowScanCheckpoint = observe;
            }

            OperationCanceledException? thrown = null;
            try
            {
                SetAdmission(index, () => cancel());
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
        Action cancel = cancellation.Cancel;
        Action<uint> observe = row =>
        {
            checkpoints.Add(row);
            if (row == cancelAt)
            {
                cancel();
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

    static List<FileEntry> Collect(FileIndex index, SearchQuery query, bool rows)
    {
        if (!rows)
        {
            return index.Enumerate(query).ToList();
        }

        var entries = new List<FileEntry>();
        foreach (var row in index.EnumerateRows(query))
        {
            entries.Add(row.ToEntry());
        }

        return entries;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamingResults_MatchSearchInOrder(bool rows)
    {
        using var tree = new TwoDriveQueryFixture();
        await using var index = await tree.OpenAsync();

        SearchQuery[] queries =
        [
            new(null), new(null, IsDirectory: false), new(null, IsDirectory: true),
            new("*.md", NameMatchMode.Glob), new("readme"),
            new("readme.md", NameMatchMode.Exact), new("re*.p?f", NameMatchMode.Glob),
            new(null, SizeMinimum: 100), new(null, IncludeDeleted: true),
            new("readme", Subtree: index.Find(Path.Combine(tree.FirstRoot, "Documents"))!.Value),
            new(null, Subtree: index.Find(Path.Combine(tree.FirstRoot, "Documents"))!.Value),
            new(null, Subtree: index.Find(Path.Combine(tree.SecondRoot, "Pictures"))!.Value)
        ];
        foreach (var query in queries)
        {
            CollectionAssert.AreEqual(
                index.Search(query).Select(entry => entry.RecordKey).ToArray(),
                Collect(index, query, rows).Select(entry => entry.RecordKey).ToArray(),
                query.ToString());
        }
    }

    [TestMethod]
    [DataRow("Search")]
    [DataRow("Enumerate")]
    [DataRow("EnumerateRows")]
    public async Task QueryArguments_AreRejectedAtCallTime(string api)
    {
        await using var index = await OpenSyntheticIndexAsync();

        void Call(SearchQuery query)
        {
            switch (api)
            {
                case "Search":
                    _ = index.Search(query);
                    break;
                case "Enumerate":
                    _ = index.Enumerate(query);
                    break;
                case "EnumerateRows":
                    _ = index.EnumerateRows(query);
                    break;
                default:
                    throw new ArgumentException($"Unknown API: {api}", nameof(api));
            }
        }

        ArgumentNullException? nullException = null;
        try
        {
            Call(null!);
        }
        catch (ArgumentNullException exception)
        {
            nullException = exception;
        }

        Assert.IsNotNull(nullException);
        Assert.AreEqual("query", nullException.ParamName);

        ArgumentOutOfRangeException? invalidException = null;
        try
        {
            Call(new SearchQuery("readme", (NameMatchMode)99));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            invalidException = exception;
        }

        Assert.IsNotNull(invalidException);
        Assert.AreEqual("query", invalidException.ParamName);
        StringAssert.StartsWith(invalidException.Message, "Unknown name match mode.");
        foreach (var block in index.CurrentSnapshot)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task StreamingExit_ReturnsEveryBorrow(bool rows, bool throwFromBody)
    {
        await using var index = await OpenSyntheticIndexAsync();
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();

        var visitCount = 0;
        int[]? countsDuringBody = null;

        void Visit()
        {
            visitCount++;
            countsDuringBody = blocks.Select(block => block.ReleaseState.Hooks.OutstandingBorrowCount).ToArray();
            if (throwFromBody)
            {
                throw new InvalidOperationException("Loop body failure.");
            }
        }

        if (throwFromBody)
        {
            InvalidOperationException? failure = null;
            try
            {
                VisitFirst(index, rows, Visit);
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            Assert.AreEqual("Loop body failure.", failure.Message);
        }
        else
        {
            VisitFirst(index, rows, Visit);
        }

        Assert.AreEqual(1, visitCount);
        CollectionAssert.AreEqual(new[] { 1, 1 }, countsDuringBody);
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount);
        }
    }

    static void VisitFirst(FileIndex index, bool rows, Action visit)
    {
        if (rows)
        {
            foreach (var row in index.EnumerateRows(new SearchQuery(null)))
            {
                _ = row.Size;
                visit();
                break;
            }
        }
        else
        {
            foreach (var entry in index.Enumerate(new SearchQuery(null)))
            {
                _ = entry.Size;
                visit();
                break;
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamingAdmission_HappensAtItsDocumentedBoundary(bool rows)
    {
        var index = await OpenSyntheticIndexAsync();
        var blocks = index.CurrentSnapshot.OrderBy(block => block.Drive.Letter).ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        AssertStreamingCancelledTiming(index, rows, token);
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount);
        }

        await index.DisposeAsync();
        AssertStreamingDisposedTiming(index, rows);
        foreach (var block in blocks)
        {
            Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount);
        }
    }

    static void AssertStreamingCancelledTiming(FileIndex index, bool rows, CancellationToken token)
    {
        if (rows)
        {
            var enumerable = index.EnumerateRows(new SearchQuery(null), token);
            OperationCanceledException? failure = null;
            try
            {
                using var enumerator = enumerable.GetEnumerator();
            }
            catch (OperationCanceledException exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure, "EnumerateRows.GetEnumerator with a cancelled token must throw OperationCanceledException.");
        }
        else
        {
            var enumerable = index.Enumerate(new SearchQuery(null), token);
            using var enumerator = enumerable.GetEnumerator();
            OperationCanceledException? failure = null;
            try
            {
                enumerator.MoveNext();
            }
            catch (OperationCanceledException exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure, "Enumerate.MoveNext with a cancelled token must throw OperationCanceledException.");
        }
    }

    static void AssertStreamingDisposedTiming(FileIndex index, bool rows)
    {
        if (rows)
        {
            var enumerable = index.EnumerateRows(new SearchQuery(null));
            ObjectDisposedException? failure = null;
            try
            {
                using var enumerator = enumerable.GetEnumerator();
            }
            catch (ObjectDisposedException exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure, "EnumerateRows.GetEnumerator on a disposed index must throw ObjectDisposedException.");
        }
        else
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => index.Enumerate(new SearchQuery(null)));
        }
    }
}

