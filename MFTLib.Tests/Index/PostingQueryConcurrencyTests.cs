using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Support = MFTLib.Tests.Index.PostingQueryTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>Readers of the posting path while a writer renames and creates rows, and readers of a released block.</summary>
[TestClass]
public sealed class PostingQueryConcurrencyTests
{
    const int ReaderCount = 8;
    const int QueriesPerReader = 150;
    const uint ChurnFirstRow = 100;
    const uint ChurnLastRow = 199;
    const uint RowCount = 3000;

    static readonly string[] QueriedNames = ["index.js", "Makefile", "README.md", "café.txt"];

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Lets the writer know the readers are finished without a disposable signal the tasks would capture.</summary>
    sealed class Finished
    {
        volatile bool _value;

        internal bool Value => _value;

        internal void Set() => _value = true;
    }

    [TestMethod]
    public async Task Readers_WhileAWriterChurnsRows_NeverMissAnUnchangedRowOrSeeADuplicate()
    {
        using var corpus = Support.BuildCorpus(seed: 11, rowCount: RowCount);
        using var opened = new Support.OpenedRoot(corpus.Builder);
        var root = opened.Root;
        var block = opened.Block;
        var untouched = QueriedNames.ToDictionary(name => name, name =>
            Support.ReferenceRows(root, new SearchQuery(name, NameMatchMode.Exact), TestContext.CancellationToken)
                .Where(row => row is < ChurnFirstRow or > ChurnLastRow).ToHashSet());
        Assert.IsTrue(untouched.Values.All(rows => rows.Count > 0), "every queried name has rows outside the churn");
        var originals = Enumerable.Range((int)ChurnFirstRow, (int)(ChurnLastRow - ChurnFirstRow + 1))
            .ToDictionary(row => (uint)row, row => NamePool.ReadRowName(block, (uint)row).ToString());
        var writer = new BlockWriter(block);
        var finished = new Finished();
        var start = new TaskCompletionSource();

        var writerTask = Task.Run(() =>
        {
            start.Task.GetAwaiter().GetResult();
            var created = block.RowCount;
            for (var round = 0; round < 200 && !finished.Value; round++)
            {
                foreach (var (row, original) in originals)
                {
                    Assert.IsTrue(writer.TryRenameRow(row, round % 2 == 0 ? $"zz-renamed-{round % 7}" : original, 1));
                }

                if (created < RowCount + 32)
                {
                    var columns = new RowColumns(1, RowFlags.InUse, 0, 1, Support.Moment.Ticks, 0);
                    Assert.IsTrue(writer.TryWriteRow(created++, QueriedNames[round % QueriedNames.Length], in columns));
                }
            }
        }, TestContext.CancellationToken);

        var readers = Enumerable.Range(0, ReaderCount).Select(_ => Task.Run(() =>
        {
            start.Task.GetAwaiter().GetResult();
            for (var query = 0; query < QueriesPerReader; query++)
            {
                var name = QueriedNames[query % QueriedNames.Length];
                var rows = SearchEngine.Search(root, new SearchQuery(name, NameMatchMode.Exact),
                    cancellationToken: TestContext.CancellationToken).Select(entry => entry.RowIndex).ToArray();
                Assert.AreEqual(rows.Length, rows.Distinct().Count(), $"duplicate rows for {name}");
                Assert.IsTrue(rows.Zip(rows.Skip(1), (left, right) => left < right).All(ascending => ascending),
                    $"rows for {name} are not ascending");
                Assert.IsEmpty(untouched[name].Except(rows).ToArray(), $"unchanged rows missed for {name}");
            }
        }, TestContext.CancellationToken)).ToArray();

        start.SetResult();
        await Task.WhenAll(readers);
        finished.Set();
        await writerTask;
    }

    [TestMethod]
    public void ExactQuery_OverAReleasedBlock_ThrowsObjectDisposedInsteadOfReadingUnmappedMemory()
    {
        using var corpus = Support.BuildCorpus(seed: 12, rowCount: 400);
        var opened = new Support.OpenedRoot(corpus.Builder);
        var block = opened.Block;
        opened.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            SearchEngine.Search(opened.Root, new SearchQuery("index.js", NameMatchMode.Exact),
                cancellationToken: TestContext.CancellationToken));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            PostingCandidates.Gather(block, [1u], TestContext.CancellationToken));
    }
}
