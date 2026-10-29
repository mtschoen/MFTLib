using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Support = MFTLib.Tests.Index.PostingQueryTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>Path lookup through postings equals the old descent, which scanned every row for each segment.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PostingFindTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The old per-segment row scan, kept here as the oracle.</summary>
    static bool ReferenceFindChild(RootBlock root, uint parentRow, string segment, out uint childRow)
    {
        var query = new SearchQuery(segment, NameMatchMode.Exact, root.Kind.IsCaseSensitive);
        using var names = new NameQueryMatches(root.Block, query, CancellationToken.None);
        var rows = root.Block.Rows;
        var found = false;
        childRow = 0;
        for (var rowIndex = 0u; rowIndex < root.Block.RowCount; rowIndex++)
        {
            ref readonly var row = ref rows[(int)rowIndex];
            if (!row.IsInUse || row.IsDeleted || row.ParentRow != parentRow || rowIndex == parentRow || !names.Matches(in row))
            {
                continue;
            }

            if (names.IsOrdinalMatch(in row))
            {
                childRow = rowIndex;
                return true;
            }

            if (!found)
            {
                found = true;
                childRow = rowIndex;
            }

            if (!names.HasOrdinalMatch)
            {
                return true;
            }
        }

        return found;
    }

    static bool ReferenceDescend(RootBlock root, string path, out uint row)
    {
        row = 0;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ReferenceFindChild(root, row, segment, out row))
            {
                return false;
            }
        }

        return true;
    }

    static IEnumerable<string> PathsOf(BlockFile block)
    {
        for (var row = Support.DirectoryCount + 1; row < block.RowCount; row++)
        {
            var name = NamePool.ReadRowName(block, row).ToString();
            var directory = NamePool.ReadRowName(block, block.Rows[(int)row].ParentRow).ToString();
            yield return $"{directory}/{name}";
            yield return $"{directory.ToUpperInvariant()}/{name.ToUpperInvariant()}";
            yield return $"{directory}/{name.ToLowerInvariant()}";
        }

        yield return "dir1/absent.bin";
        yield return "absent/index.js";
        yield return "dir1";
    }

    void AssertPathsAgree(Support.Corpus corpus, string label)
    {
        using var opened = new Support.OpenedRoot(corpus.Builder);
        var compared = 0;
        foreach (var path in PathsOf(opened.Block))
        {
            var expected = ReferenceDescend(opened.Root, path, out var expectedRow);
            var actual = LookupEngine.TryDescend(opened.Root, 0, path, TestContext.CancellationToken, out var actualRow);
            Assert.AreEqual(expected, actual, $"{label} {path}");
            if (expected)
            {
                Assert.AreEqual(expectedRow, actualRow, $"{label} {path}");
            }

            compared++;
        }

        Assert.IsGreaterThan(1000, compared);
    }

    [TestMethod]
    public void TryDescend_OverAFreshBase_EqualsTheRowScanDescent()
    {
        using var corpus = Support.BuildCorpus(seed: 21, rowCount: 1500);
        AssertPathsAgree(corpus, "fresh");
    }

    [TestMethod]
    public void TryDescend_AfterRenamesWithDeltas_EqualsTheRowScanDescent()
    {
        using var corpus = Support.BuildCorpus(seed: 22, rowCount: 1500);
        Support.Churn(corpus, seed: 22, changes: 200);
        AssertPathsAgree(corpus, "churned");
    }

    [TestMethod]
    public void TryDescend_PrefersTheExactSpellingThenTheLowestRow_ThroughPostings()
    {
        using var builder = CaseCollidingBuilder(ProducerKind.Mft, out var directory, out var upper, out var lower);
        using var opened = new Support.OpenedRoot(builder);
        Assert.IsFalse(opened.Root.Kind.IsCaseSensitive, "an MFT block folds case on every OS");

        Assert.IsTrue(LookupEngine.TryDescend(opened.Root, 0, "d/readme", TestContext.CancellationToken, out var exact));
        Assert.IsTrue(LookupEngine.TryDescend(opened.Root, 0, "d/README", TestContext.CancellationToken, out var exactUpper));
        Assert.IsTrue(LookupEngine.TryDescend(opened.Root, 0, "d/ReadMe", TestContext.CancellationToken, out var neither));

        Assert.AreEqual(lower, exact);
        Assert.AreEqual(upper, exactUpper);
        Assert.AreEqual(upper, neither, "no exact spelling falls back to the lowest row");
        Assert.AreEqual(1u, directory);
    }

    [TestMethod]
    public void TryDescend_OverACaseSensitiveRoot_FindsOnlyTheExactSpelling()
    {
        RootKind._directoryCaseSensitiveOverride = true;
        using var builder = CaseCollidingBuilder(ProducerKind.Enumeration, out _, out var upper, out var lower);
        using var opened = new Support.OpenedRoot(builder);
        Assert.IsTrue(opened.Root.Kind.IsCaseSensitive);

        Assert.IsTrue(LookupEngine.TryDescend(opened.Root, 0, "d/readme", TestContext.CancellationToken, out var exact));
        Assert.IsTrue(LookupEngine.TryDescend(opened.Root, 0, "d/README", TestContext.CancellationToken, out var exactUpper));

        Assert.AreEqual(lower, exact);
        Assert.AreEqual(upper, exactUpper);
        Assert.IsFalse(LookupEngine.TryDescend(opened.Root, 0, "d/ReadMe", TestContext.CancellationToken, out _),
            "a case-sensitive root has no fallback to a differently spelled name");
    }

    [TestCleanup]
    public void Cleanup() => RootKind._directoryCaseSensitiveOverride = null;

    static SyntheticBlockBuilder CaseCollidingBuilder(ProducerKind producerKind, out uint directory, out uint upper, out uint lower)
    {
        var builder = new SyntheticBlockBuilder(slotCapacity: 64, namePoolCapacity: 4096);
        try
        {
            if (producerKind == ProducerKind.Mft)
            {
                builder.MutateHeader((ref header) =>
                {
                    header.ProducerKind = ProducerKind.Mft;
                    header.ScanShape = SyntheticScanShape.Default(ProducerKind.Mft);
                });
            }

            var root = builder.AddRoot();
            directory = builder.AddRow("d", root, RowFlags.InUse | RowFlags.Directory, 0, Support.Moment, 0);
            upper = builder.AddRow("README", directory, RowFlags.InUse, 1, Support.Moment, 0);
            lower = builder.AddRow("readme", directory, RowFlags.InUse, 1, Support.Moment, 0);
            builder.AddRow("readme", root, RowFlags.InUse, 1, Support.Moment, 0);
            builder.Complete(Support.Moment);
            return builder;
        }
        catch
        {
            builder.Dispose();
            throw;
        }
    }
}
