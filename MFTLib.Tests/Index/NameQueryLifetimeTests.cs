using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>The entry pass of a name query: when it observes cancellation, and what a concurrent rename can do to it.</summary>
[TestClass]
public sealed class NameQueryLifetimeTests
{
    const int NameCount = 10_000;
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    static SyntheticBlockBuilder BuildBlock()
    {
        var builder = new SyntheticBlockBuilder(slotCapacity: 16384, namePoolCapacity: 1 << 20);
        var root = builder.AddRoot();
        for (var index = 0; index < NameCount; index++)
        {
            builder.AddRow($"file-{index}.dat", root, RowFlags.InUse, 1, Moment, 0);
        }

        return builder;
    }

    [TestMethod]
    public void EntryPass_ObservesCancellationBeforeSetupAndEvery4096Entries()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        var checks = 0;
        using (new NameQueryMatches(block, new SearchQuery("dat"), () => checks++,
                   TestContext.CancellationToken))
        {
        }

        // One before setup, one once the count is fixed, one at entries 4096 and 8192, and one when the pass ends.
        Assert.AreEqual(2 + (NameCount / (int)NameQueryMatches.CancellationCheckIntervalEntries) + 1, checks);
    }

    [TestMethod]
    public void EntryPass_CancelledMidPass_StopsAtTheNextCheckpoint()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        using var source = new CancellationTokenSource();
        var checks = 0;
        void Observe()
        {
            if (++checks == 3)
            {
                source.Cancel();
            }
        }

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new NameQueryMatches(block, new SearchQuery("dat"), Observe, source.Token));
        Assert.AreEqual(3, checks);
    }

    [TestMethod]
    public void EntryPass_AlreadyCancelled_ThrowsBeforeReadingAnyEntry()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var accessesBefore = block.NameRegionAccessCount;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new NameQueryMatches(block, new SearchQuery("dat"), source.Token));
        Assert.AreEqual(accessesBefore, block.NameRegionAccessCount);
    }

    [TestMethod]
    [DataRow(NameMatchMode.Substring, false)]
    [DataRow(NameMatchMode.Exact, false)]
    [DataRow(NameMatchMode.Exact, true)]
    public void RenameToANewNameDuringAPass_IsNotMatchedAndDoesNotFault(NameMatchMode mode, bool caseSensitive)
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        const uint renamedRow = 3;
        var checks = 0;
        var renamed = false;
        void Observe()
        {
            // The second checkpoint is after the entry count was captured and before any chain is set up.
            if (++checks == 2)
            {
                renamed = true;
                Assert.IsTrue(writer.TryRenameRow(renamedRow, "zzz-interned-during-the-pass", parentRow: 0));
            }
        }

        var pattern = mode == NameMatchMode.Exact ? "zzz-interned-during-the-pass" : "zzz";
        using var names = new NameQueryMatches(block, new SearchQuery(pattern, mode, caseSensitive), Observe,
            TestContext.CancellationToken);

        Assert.IsTrue(renamed);
        Assert.AreEqual("zzz-interned-during-the-pass",
            MFTLibTestExtensions.SyntheticNamePool.ReadRowName(block, renamedRow).ToString());
        Assert.IsFalse(names.Matches(in block.Rows[(int)renamedRow]));
        Assert.IsNull(MFTLibTestExtensions.SyntheticNamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void DisposingTheDefaultEnumerator_IsHarmless()
    {
        var enumerator = default(IndexRowEnumerator);

        enumerator.Dispose();
        enumerator.Dispose();
    }

    [TestMethod]
    public void Dispose_ReturnsTheBitsOnceAndLeavesNothingMatching()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        var names = new NameQueryMatches(block, new SearchQuery("file-7"),
            TestContext.CancellationToken);
        Assert.IsTrue(names.Matches(in block.Rows[8]));

        names.Dispose();
        names.Dispose();

        Assert.IsFalse(names.Matches(in block.Rows[8]));
    }

    [TestMethod]
    public void ExactIgnoreCase_CollectsEveryCasingAndRemembersTheOrdinalSpelling()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        var upper = builder.AddRow("README.md", root, RowFlags.InUse, 1, Moment, 0);
        var lower = builder.AddRow("readme.md", root, RowFlags.InUse, 1, Moment, 0);
        var other = builder.AddRow("notes.md", root, RowFlags.InUse, 1, Moment, 0);
        var block = builder.OpenForWriting();

        using var names = new NameQueryMatches(block, new SearchQuery("readme.md", NameMatchMode.Exact),
            TestContext.CancellationToken);

        Assert.IsTrue(names.Matches(in block.Rows[(int)upper]));
        Assert.IsTrue(names.Matches(in block.Rows[(int)lower]));
        Assert.IsFalse(names.Matches(in block.Rows[(int)other]));
        Assert.IsTrue(names.HasOrdinalMatch);
        Assert.IsTrue(names.IsOrdinalMatch(in block.Rows[(int)lower]));
        Assert.IsFalse(names.IsOrdinalMatch(in block.Rows[(int)upper]));
    }
}
