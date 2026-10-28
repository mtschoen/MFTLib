using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class SubtreeMembershipTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    const RowFlags Directory = RowFlags.InUse | RowFlags.Directory;

    static BlockFile Open(SyntheticBlockBuilder builder)
    {
        builder.Complete(Moment);
        return builder.OpenForReading(out _)!;
    }

    // The oracle is a climb written independently of the production one: it follows parents with a visited set,
    // and reports the three outcomes the cache must preserve (inside, outside, and a chain still going after the cap,
    // which includes a tail that enters a cycle only beyond the cap).
    static bool? Oracle(BlockFile block, uint row, uint anchor)
    {
        var visited = new HashSet<uint>();
        var current = row;
        for (var hops = 0; hops <= BlockLayout.MaximumPathDepth; hops++)
        {
            if (current == anchor)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                return false;
            }

            var parent = block.Rows[(int)current].ParentRow;
            if (parent == current || parent >= block.Header.RowCount)
            {
                return false;
            }

            current = parent;
        }

        return null;
    }

    [TestMethod]
    public void Contains_AgreesWithTheParentChainOnEveryRow()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 4096, namePoolCapacity: 65536);
        var root = builder.AddRoot();
        var rows = new List<uint> { root };
        for (var index = 1; index < 2000; index++)
        {
            rows.Add(builder.AddRow($"r{index}", rows[(index - 1) / 2], index % 3 == 0 ? RowFlags.InUse : Directory, 1,
                Moment, sequenceNumber: 0));
        }

        var detached = builder.AddRow("detached", (uint)rows.Count + 900, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        var cycleFirst = builder.AddRow("cycleA", detached + 2, Directory, 0, Moment, sequenceNumber: 0);
        var cycleSecond = builder.AddRow("cycleB", cycleFirst, Directory, 0, Moment, sequenceNumber: 0);
        // A two-row cycle entered by a short tail (answered outside, cycle detected within the cap) and by a long tail
        // (still going after the cap, so it throws), plus the chain below.
        var shortTail = new List<uint>();
        var longTail = new List<uint>();
        var shortParent = cycleSecond;
        var longParent = cycleSecond;
        for (var depth = 0; depth < 140; depth++)
        {
            longParent = builder.AddRow($"lt{depth}", longParent, Directory, 0, Moment, sequenceNumber: 0);
            longTail.Add(longParent);
            if (depth < 100)
            {
                shortParent = builder.AddRow($"st{depth}", shortParent, Directory, 0, Moment, sequenceNumber: 0);
                shortTail.Add(shortParent);
            }
        }

        var chain = new List<uint>();
        var parent = root;
        for (var depth = 0; depth < 130; depth++)
        {
            parent = builder.AddRow($"c{depth}", parent, Directory, 0, Moment, sequenceNumber: 0);
            chain.Add(parent);
        }

        using var block = Open(builder);
        var rowCount = block.Header.RowCount;
        uint[] anchors = [root, rows[3], rows[100], cycleFirst, chain[10], shortTail[5]];
        var ascending = Enumerable.Range(0, (int)rowCount).Select(row => (uint)row).ToArray();
        var descending = ascending.Reverse().ToArray();
        var shuffled = ascending.OrderBy(row => (row * 2654435761u) % 4093).ToArray();
        foreach (var order in new[] { ascending, descending, shuffled })
        {
            foreach (var anchor in anchors)
            {
                using var membership = new SubtreeMembership(block, anchor);
                foreach (var row in order)
                {
                    var expected = Oracle(block, row, anchor);
                    if (expected is { } verdict)
                    {
                        Assert.AreEqual(verdict, membership.Contains(row), $"row {row} under anchor {anchor}");
                    }
                    else
                    {
                        Assert.ThrowsExactly<InvalidDataException>(() => membership.Contains(row),
                            $"row {row} under anchor {anchor} is deeper than the cap");
                    }
                }
            }
        }

        Assert.IsTrue(cycleSecond > cycleFirst);
        Assert.IsNotEmpty(longTail);
    }

    [TestMethod]
    public void Contains_ClimbsEachDirectoryOncePerQuery()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 4096, namePoolCapacity: 65536);
        var root = builder.AddRoot();
        var directory = root;
        for (var depth = 0; depth < 100; depth++)
        {
            directory = builder.AddRow($"d{depth}", directory, Directory, 0, Moment, sequenceNumber: 0);
        }

        for (var file = 0; file < 1000; file++)
        {
            builder.AddRow($"f{file}", directory, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        }

        using var block = Open(builder);
        using var membership = new SubtreeMembership(block, root);
        var rowCount = block.Header.RowCount;
        for (uint row = 0; row < rowCount; row++)
        {
            Assert.AreEqual(Oracle(block, row, root) == true, membership.Contains(row));
        }

        Assert.IsLessThanOrEqualTo(rowCount + 100, membership.ParentReads);
    }

    [TestMethod]
    public void Contains_ConcurrentPartitionsAgreeWithASequentialPass()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 4096, namePoolCapacity: 65536);
        var root = builder.AddRoot();
        var rows = new List<uint> { root };
        for (var index = 1; index < 3000; index++)
        {
            rows.Add(builder.AddRow($"r{index}", rows[(index - 1) / 2], Directory, 0, Moment, sequenceNumber: 0));
        }

        using var block = Open(builder);
        var anchor = rows[10];
        var rowCount = (int)block.Header.RowCount;
        var expected = new bool[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            using var fresh = new SubtreeMembership(block, anchor);
            expected[row] = fresh.Contains((uint)row);
        }

        var actual = new bool[rowCount];
        using (var shared = new SubtreeMembership(block, anchor))
        {
            ContainsInParallel(shared, actual, TestContext.CancellationToken);
        }

        CollectionAssert.AreEqual(expected, actual);
    }

    static void ContainsInParallel(SubtreeMembership membership, bool[] verdicts, CancellationToken cancellationToken) =>
        Parallel.For(0, verdicts.Length, new ParallelOptions { CancellationToken = cancellationToken },
            row => verdicts[row] = membership.Contains((uint)row));

    [TestMethod]
    public void Contains_RowPastTheCapturedCount_IsAnsweredWithoutCaching()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 512, namePoolCapacity: 4096);
        var root = builder.AddRoot();
        builder.AddRow("a", root, Directory, 0, Moment, sequenceNumber: 0);

        using var block = Open(builder);
        using var membership = new SubtreeMembership(block, root);
        var pastTheEnd = block.Header.RowCount + 5;

        var first = membership.Contains(pastTheEnd);
        var readsAfterFirst = membership.ParentReads;
        var second = membership.Contains(pastTheEnd);

        Assert.AreEqual(first, second);
        Assert.IsGreaterThan(readsAfterFirst, membership.ParentReads);
    }

    public TestContext TestContext { get; set; } = null!;
}
