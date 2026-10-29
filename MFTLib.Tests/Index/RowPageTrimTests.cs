using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A row scan drops its partition's row pages from the working set (issue 861). The per-block trim hook is the
///     seam, so these tests need no process-global hooks and run in parallel with the rest of the suite.
/// </summary>
[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                    "throughout rather than IDisposable on the test class itself.")]
public class RowPageTrimTests
{
    static readonly DateTime Moment = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder? _builder;
    RootBlock? _rootBlock;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_rootBlock is not null)
        {
            await _rootBlock.ReleaseNowAsync();
        }

        _builder?.Dispose();
    }

    RootBlock Build(uint rowCount)
    {
        _builder = new SyntheticBlockBuilder(slotCapacity: rowCount, namePoolCapacity: rowCount * 16 + 4096);
        var root = _builder.AddRoot();
        for (var row = 1u; row < rowCount; row++)
        {
            _builder.AddRow($"file-{row}", root, RowFlags.InUse, row, Moment, sequenceNumber: 0);
        }

        _builder.Complete(Moment);
        _rootBlock = new RootBlock(new VolumeKey('T'), _builder.OpenForReading(out _)!);
        return _rootBlock;
    }

    static List<(long Start, long End)> Record(RootBlock rootBlock)
    {
        var ranges = new List<(long Start, long End)>();
        rootBlock.Block.Hooks._trimViewRange = (start, end) =>
        {
            lock (ranges)
            {
                ranges.Add((start, end));
            }

            return 0;
        };
        return ranges;
    }

    static long RowOffset(uint row) => BlockLayout.RowRegionOffset + (long)row * BlockLayout.RowBytes;

    [TestMethod]
    public void Search_SinglePartition_TrimsTheWholePagesInsideTheRowRange()
    {
        var rootBlock = Build(300);
        var ranges = Record(rootBlock);
        var pageBytes = (long)Environment.SystemPageSize;

        var results = SearchEngine.Search(rootBlock, new SearchQuery("file-29"), cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(11, results);
        var expectedStart = (RowOffset(0) + pageBytes - 1) / pageBytes * pageBytes;
        var expectedEnd = RowOffset(300) / pageBytes * pageBytes;
        if (expectedEnd <= expectedStart)
        {
            Assert.IsEmpty(ranges);
            return;
        }

        Assert.AreSequenceEqual([(expectedStart, expectedEnd)], ranges);
    }

    [TestMethod]
    public void Search_ManyPartitions_TrimsEachPartitionInsideItsOwnPagesAndKeepsResults()
    {
        var rowCount = ScanPartitioning.SingleThreadedRowThreshold + 5000;
        var rootBlock = Build(rowCount);
        var ranges = Record(rootBlock);
        var query = new SearchQuery("file-1*", NameMatchMode.Glob);
        var expected = SearchEngine.Enumerate(rootBlock, query, cancellationToken: TestContext.CancellationToken)
            .Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(expected);

        var results = SearchEngine.Search(rootBlock, query, cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(expected, results.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray());
        var partitions = ScanPartitioning.Partition(rowCount, ScanPartitioning.DefaultPartitionCount(rowCount));
        var pageBytes = (long)Environment.SystemPageSize;
        Assert.HasCount(partitions.Count, ranges);
        foreach (var (start, end) in ranges)
        {
            Assert.AreEqual(0, start % pageBytes);
            Assert.AreEqual(0, end % pageBytes);
            Assert.IsLessThan(end, start);
            Assert.IsTrue(partitions.Any(partition =>
                    start >= RowOffset(partition.StartRow) && end <= RowOffset(partition.EndRowExclusive)),
                "A trim must stay inside one partition's own byte span so it never reaches a neighbour's pages.");
        }

        var ordered = ranges.Order().ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            Assert.IsLessThanOrEqualTo(ordered[index].Start, ordered[index - 1].End, "Trims must not overlap.");
        }
    }

    [TestMethod]
    public void Search_ReleasedBlock_IsNotTrimmed()
    {
        var rootBlock = Build(300);
        var ranges = Record(rootBlock);
        rootBlock.ReleaseNowAsync().GetAwaiter().GetResult();

        SearchEngine.ReleaseScannedPages(rootBlock, (0, 300));

        Assert.IsEmpty(ranges);
    }

    [TestMethod]
    public void TryReleaseRowPages_DisposedBlock_DoesNotTrim()
    {
        using var directory = new TempDirectory("mftlib-trim-disposed");
        var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: 512);
        var calls = 0;
        block.Hooks._trimViewRange = (_, _) => ++calls;
        block.Dispose();

        Assert.IsFalse(block.TryReleaseRowPages(0, 512));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void TryReleaseRowPages_InvalidOrSubPageRange_ReportsWithoutCallingNative()
    {
        using var directory = new TempDirectory("mftlib-trim-range");
        using var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: 512);
        var calls = 0;
        block.Hooks._trimViewRange = (_, _) => ++calls;

        Assert.IsFalse(block.TryReleaseRowPages(10, 5));
        Assert.IsFalse(block.TryReleaseRowPages(0, 513));
        Assert.IsTrue(block.TryReleaseRowPages(1, 3), "A range without a whole page has nothing to trim.");
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void TryReleaseRowPages_NativeFailure_ReportsFalse()
    {
        using var directory = new TempDirectory("mftlib-trim-failure");
        using var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: 512);
        block.Hooks._trimViewRange = (_, _) => 87;

        Assert.IsFalse(block.TryReleaseRowPages(0, 512));
    }

    [TestMethod]
    public void CollectPartition_CancelledScan_PropagatesAndStillTrims()
    {
        var rootBlock = Build(300);
        var ranges = Record(rootBlock);
        var query = new SearchQuery("file");
        using var names = new NameQueryMatches(rootBlock.Block, query, CancellationToken.None);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var scan = new SearchEngine.RootScan(rootBlock, query, null);

        Assert.Throws<OperationCanceledException>(() =>
            SearchEngine.CollectPartition(scan, names, (0, 300), [], source.Token));

        Assert.HasCount(1, ranges);
    }

    [TestMethod]
    public void Search_ConcurrentSearchesWithTheRealTrim_ReturnIdenticalResults()
    {
        var rowCount = ScanPartitioning.SingleThreadedRowThreshold + 5000;
        var rootBlock = Build(rowCount);
        var query = new SearchQuery("file-4*", NameMatchMode.Glob);
        var expected = SearchEngine.Search(rootBlock, query, cancellationToken: TestContext.CancellationToken)
            .Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
        var failures = new List<string>();

        Parallel.For(0, 8, _ =>
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var names = SearchEngine.Search(rootBlock, query, cancellationToken: TestContext.CancellationToken)
                    .Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
                if (!names.SequenceEqual(expected))
                {
                    lock (failures)
                    {
                        failures.Add($"{names.Length} results, expected {expected.Length}");
                    }
                }
            }
        });

        Assert.IsEmpty(failures);
    }

    [TestMethod]
    public void TryReleaseRowPages_WrittenRowsStayIntactAndPersist()
    {
        using var directory = new TempDirectory("mftlib-trim-dirty");
        var path = Path.Combine(directory.Path, "T-0BADF00D.mlix");
        const uint rows = 4096;
        using (var block = TestBlocks.Create(path, slotCapacity: rows, namePoolCapacity: rows * 16))
        {
            var writer = new BlockWriter(block);
            for (var row = 0u; row < rows; row++)
            {
                Assert.IsTrue(writer.TryWriteRow(row, $"r{row}",
                    new RowColumns(0, RowFlags.InUse, 0, row + 1, Moment.Ticks, 0)));
                if (row % 512 == 511)
                {
                    Assert.IsTrue(block.TryReleaseRowPages(row - 511, row + 1), "Real trim over freshly dirtied pages.");
                }
            }

            Assert.IsTrue(block.TryReleaseRowPages(0, rows));
            for (var row = 0; row < rows; row++)
            {
                Assert.AreEqual(row + 1, block.Rows[row].Size);
            }

            block.Flush(null);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        for (var row = 0; row < rows; row++)
        {
            var size = MemoryMarshal.Read<FileRow>(bytes.AsSpan((int)RowOffset((uint)row))).Size;
            Assert.AreEqual(row + 1, size);
        }
    }
}
