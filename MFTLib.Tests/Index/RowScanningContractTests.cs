using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     One contract per behavior, run over all six row-scanning operations: results across a multi-root
///     index, call-time argument checks, cancellation, disposal, and the snapshot borrow each holds.
/// </summary>
[TestClass]
public sealed class RowScanningContractTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    static readonly CancellationToken Cancelled = new(true);
    const string BodyFailureMessage = "Loop body failure.";

    public delegate int QueryCall(FileIndex index, FileEntry root, string path, CancellationToken token);

    public static IEnumerable<object[]> ApiCases()
    {
        yield return ["Find", (QueryCall)((index, _, path, token) => index.Find(path, token).HasValue ? 1 : 0), 1];
        yield return ["Search", (QueryCall)((index, _, _, token) => index.Search(new SearchQuery("readme"), token).Count), 2];
        yield return ["Enumerate", (QueryCall)((index, _, _, token) => index.Enumerate(new SearchQuery("readme"), token).Count()), 2];
        yield return ["EnumerateRows", (QueryCall)((index, _, _, token) => CountRows(index, new SearchQuery("readme"), token)), 2];
        yield return ["Root", (QueryCall)((index, _, _, token) => index.Root(token).IsValid ? 1 : 0), 1];
        yield return ["Children", (QueryCall)((_, root, _, token) => root.Children(token).Count), 2];
    }

    sealed class BodyRecord
    {
        public int Visits;
        public int[] Borrows = [];
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

    static Type? ThrownBy(QueryCall call, FileIndex index, FileEntry root, string path, CancellationToken token)
    {
        try
        {
            call(index, root, path, token);
            return null;
        }
        catch (Exception exception)
        {
            return exception.GetType();
        }
    }

    static Exception? ThrownByBegin(Action<FileIndex, SearchQuery?> begin, FileIndex index, SearchQuery? query)
    {
        try
        {
            begin(index, query);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // Names the step that failed: creating the sequence, getting its enumerator, or the first MoveNext.
    // A cancelAfterAdmission source is cancelled between GetEnumerator and the first MoveNext.
    static (string Step, Type? Error) Probe(FileIndex index, bool rows, CancellationToken token,
        CancellationTokenSource? cancelAfterAdmission = null)
    {
        var step = "Create";
        try
        {
            if (rows)
            {
                var sequence = index.EnumerateRows(new SearchQuery(null), token);
                step = "GetEnumerator";
                using var enumerator = sequence.GetEnumerator();
                cancelAfterAdmission?.Cancel();
                step = "MoveNext";
                enumerator.MoveNext();
            }
            else
            {
                var sequence = index.Enumerate(new SearchQuery(null), token);
                step = "GetEnumerator";
                using var enumerator = sequence.GetEnumerator();
                cancelAfterAdmission?.Cancel();
                step = "MoveNext";
                enumerator.MoveNext();
            }

            return ("None", null);
        }
        catch (Exception exception)
        {
            return (step, exception.GetType());
        }
    }

    static void VisitFirst(FileIndex index, bool rows, bool throwFromBody, BodyRecord record)
    {
        if (rows)
        {
            foreach (var row in index.EnumerateRows(new SearchQuery(null)))
            {
                _ = row.Size;
                RecordVisit(index, throwFromBody, record);
                break;
            }
        }
        else
        {
            foreach (var entry in index.Enumerate(new SearchQuery(null)))
            {
                _ = entry.Size;
                RecordVisit(index, throwFromBody, record);
                break;
            }
        }
    }

    static void RecordVisit(FileIndex index, bool throwFromBody, BodyRecord record)
    {
        record.Visits++;
        record.Borrows = index.CurrentBlock is { } block ? [block.ReleaseState.Hooks.OutstandingBorrowCount] : [];
        if (throwFromBody)
        {
            throw new InvalidOperationException(BodyFailureMessage);
        }
    }

    static Task<FileIndex> OpenSyntheticAsync(uint rowCount, Func<uint, string> fileNameFactory,
        Func<uint, uint>? parentFactory = null) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(
                    SeededBlocks.Build(request, rowCount, fileNameFactory, FixedMoment, parentFactory),
                    SkippedRecordCount: 0)))
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), AppContext.BaseDirectory, 1)), CancellationToken.None);

    // Consumes a stream and cancels its token after cancelAfter items; returns how many items were yielded.
    static int VisitUntilCancelled(FileIndex index, bool rows, SearchQuery query, int cancelAfter,
        CancellationTokenSource cancellation, out bool cancelled)
    {
        var visited = 0;
        cancelled = false;
        try
        {
            if (rows)
            {
                foreach (var row in index.EnumerateRows(query, cancellation.Token))
                {
                    _ = row.Size;
                    if (++visited == cancelAfter)
                    {
                        cancellation.Cancel();
                    }
                }
            }
            else
            {
                foreach (var entry in index.Enumerate(query, cancellation.Token))
                {
                    _ = entry.Size;
                    if (++visited == cancelAfter)
                    {
                        cancellation.Cancel();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        return visited;
    }

    static int BorrowCount(FileIndex index) =>
        index.CurrentBlock is { } block ? block.ReleaseState.Hooks.OutstandingBorrowCount : 0;

    [TestMethod]
    public async Task StreamingResults_MatchSearchAcrossAMultiRootIndex()
    {
        using var tree = new TwoRootQueryFixture();
        var set = await tree.OpenSetAsync();
        var documents = set.Find(Path.Combine(tree.FirstRoot, "Documents"), TestContext.CancellationToken)!.Value;
        var pictures = set.Find(Path.Combine(tree.SecondRoot, "Pictures"), TestContext.CancellationToken)!.Value;

        SearchQuery[] queries =
        [
            new(null), new(null, IsDirectory: false), new(null, IsDirectory: true),
            new("*.md", NameMatchMode.Glob), new("readme"), new("readme.md", NameMatchMode.Exact),
            new("re*.p?f", NameMatchMode.Glob), new(null, SizeMinimum: 100), new(null, IncludeDeleted: true),
            new("readme", Subtree: documents), new(null, Subtree: documents), new(null, Subtree: pictures)
        ];
        foreach (var query in queries)
        {
            var expected = set.Search(query, TestContext.CancellationToken).Select(entry => entry.RecordKey).ToArray();
            Assert.AreSequenceEqual(expected, [.. set.Enumerate(query, TestContext.CancellationToken).Select(entry => entry.RecordKey)], query.ToString());
            var rows = new List<IndexRecordKey>();
            foreach (var row in set.EnumerateRows(query, TestContext.CancellationToken))
            {
                rows.Add(row.ToEntry().RecordKey);
            }

            Assert.AreSequenceEqual(expected, rows, query.ToString());
        }
    }

    [TestMethod]
    public async Task UndefinedMatchModeAndNullQuery_ThrowAtCallTime()
    {
        using var tree = new TwoRootQueryFixture();
        await using var index = await tree.OpenAsync();
        Action<FileIndex, SearchQuery?>[] begins =
        [
            (target, query) => target.Search(query!, TestContext.CancellationToken),
            (target, query) => target.Enumerate(query!, TestContext.CancellationToken),
            (target, query) => target.EnumerateRows(query!, TestContext.CancellationToken)
        ];

        foreach (var begin in begins)
        {
            var missing = ThrownByBegin(begin, index, null) as ArgumentNullException;
            Assert.AreEqual("query", missing?.ParamName);
            var invalid = ThrownByBegin(begin, index, new SearchQuery("readme", (NameMatchMode)99)) as ArgumentOutOfRangeException;
            Assert.AreEqual("query", invalid?.ParamName);
            Assert.StartsWith("Unknown name match mode.", invalid!.Message);
        }

        Assert.AreEqual(0, BorrowCount(index));
    }

    [TestMethod]
    [DynamicData(nameof(ApiCases))]
    public async Task CancelledToken_Throws_AndTakesNoBorrow(string api, QueryCall call, int expectedCount)
    {
        using var tree = new TwoRootQueryFixture();
        await using var index = await tree.OpenAsync();

        Assert.AreEqual(typeof(OperationCanceledException),
            ThrownBy(call, index, index.Root(TestContext.CancellationToken), Path.Combine(tree.FirstRoot, "Documents", "readme.md"), Cancelled), api);
        Assert.AreEqual(0, BorrowCount(index), api);
    }

    [TestMethod]
    [DynamicData(nameof(ApiCases))]
    public async Task DisposedIndex_Rejects(string api, QueryCall call, int expectedCount)
    {
        using var tree = new TwoRootQueryFixture();
        var index = await tree.OpenAsync();
        var root = index.Root(TestContext.CancellationToken);
        var path = Path.Combine(tree.FirstRoot, "Documents", "readme.md");
        var block = index.CurrentBlock!;
        await index.DisposeAsync();

        Assert.AreEqual(typeof(ObjectDisposedException), ThrownBy(call, index, root, path, CancellationToken.None), api);
        Assert.AreEqual(0, block.ReleaseState.Hooks.OutstandingBorrowCount, api);
    }

    [TestMethod]
    [DynamicData(nameof(ApiCases))]
    public async Task CompletedOperation_ReturnsItsBorrow(string api, QueryCall call, int expectedCount)
    {
        using var tree = new TwoRootQueryFixture();
        await using var index = await tree.OpenAsync();

        var path = Path.Combine(tree.FirstRoot, "Documents", "readme.md");
        Assert.AreEqual(expectedCount, call(index, index.Root(TestContext.CancellationToken), path, CancellationToken.None), api);
        Assert.AreEqual(0, BorrowCount(index), api);
    }

    // Enumerate is lazy until MoveNext for a cancelled token but rejects a disposed index when called;
    // EnumerateRows rejects both at GetEnumerator.
    [TestMethod]
    [DataRow(false, "MoveNext", "Create")]
    [DataRow(true, "GetEnumerator", "GetEnumerator")]
    public async Task StreamingExceptionBoundaries_AreDocumented(bool rows, string cancelledStep, string disposedStep)
    {
        using var tree = new TwoRootQueryFixture();
        var index = await tree.OpenAsync();

        Assert.AreEqual((cancelledStep, typeof(OperationCanceledException)), Probe(index, rows, Cancelled));
        Assert.AreEqual(0, BorrowCount(index));
        using (var cancellation = new CancellationTokenSource())
        {
            Assert.AreEqual(("MoveNext", typeof(OperationCanceledException)),
                Probe(index, rows, cancellation.Token, cancellation), "a token cancelled after admission yielded a row");
        }

        Assert.AreEqual(0, BorrowCount(index));
        Assert.AreEqual(("None", null), Probe(index, rows, CancellationToken.None));

        await index.DisposeAsync();
        Assert.AreEqual((disposedStep, typeof(ObjectDisposedException)), Probe(index, rows, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task StreamingExitedPartway_ReturnsItsBorrow(bool rows, bool throwFromBody)
    {
        using var tree = new TwoRootQueryFixture();
        await using var index = await tree.OpenAsync();
        var record = new BodyRecord();
        var failed = false;

        try
        {
            VisitFirst(index, rows, throwFromBody, record);
        }
        catch (InvalidOperationException exception) when (exception.Message == BodyFailureMessage)
        {
            failed = true;
        }

        Assert.AreEqual(throwFromBody, failed);
        Assert.AreEqual(1, record.Visits);
        Assert.HasCount(1, record.Borrows);
        Assert.AreSequenceEqual([1], record.Borrows);
        Assert.AreEqual(0, index.CurrentBlock!.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    // RowScanner has no per-yield check, so the rows yielded before the exception bound its checkpoint cadence.
    [TestMethod]
    public async Task EnumerateRows_CancelledMidScan_StopsWithinTheDocumentedRowBound()
    {
        await using var index = await OpenSyntheticAsync(9000, row => $"file{row}.dat");
        using var cancellation = new CancellationTokenSource();

        var visited = VisitUntilCancelled(index, true, new SearchQuery(null), 3, cancellation, out var cancelled);

        Assert.IsTrue(cancelled);
        Assert.IsLessThanOrEqualTo(4096, visited, $"yielded {visited} rows; the contract is one check at least every 4096 rows");
        Assert.AreEqual(0, BorrowCount(index));
    }

    // Rows 6-133 chain one directory per row below the root; row 4300 is a matching file under the last
    // of them, so its subtree check exceeds the path depth and throws InvalidDataException before the
    // per-yield token check. Only the root-scan checkpoint at row 4096 can end the scan cancelled.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamingCancelledAfterTheFirstMatch_StopsAtTheRowCheckpointBeforeTheDeepSentinel(bool rows)
    {
        const uint chainEnd = 6 + BlockLayout.MaximumPathDepth - 1;
        const uint sentinel = 4300;
        await using var index = await OpenSyntheticAsync(4400,
            row => row == chainEnd + 1 ? "needle.dat" : row == sentinel ? "needle-deep.dat" : $"file{row}.dat",
            row => row == sentinel ? chainEnd : row is > 6 and <= chainEnd ? row - 1 : 5);
        using var cancellation = new CancellationTokenSource();

        var query = new SearchQuery("needle", Subtree: index.Root(TestContext.CancellationToken));
        var visited = VisitUntilCancelled(index, rows, query, 1, cancellation, out var cancelled);

        Assert.IsTrue(cancelled);
        Assert.AreEqual(1, visited);
        Assert.AreEqual(0, BorrowCount(index));
    }

    public TestContext TestContext { get; set; } = null!;
}
