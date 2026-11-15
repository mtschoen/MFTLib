using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: a compacting copy stores only the names some row references, renumbered in ascending order,
///     and every row, tombstoned, delete-pending and freed rows included, reads the same name afterwards. The verbatim
///     copy, which enumeration growth uses, keeps every name and identifier.
/// </summary>
[TestClass]
public sealed class BlockCopierCompactionTests
{
    static readonly DateTime Moment = new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
    static readonly RowColumns FileColumns = new(0, RowFlags.InUse, 0, 1, Moment.Ticks, 0);

    /// <summary>
    ///     Rows 5 to 12 of a completed block, then renames that leave <c>old-*.txt</c>, <c>Makefile</c> and
    ///     <c>caf&#233;-old.txt</c> referenced by no row; row 9 is tombstoned, row 10 freed and row 12 delete-pending.
    /// </summary>
    static BlockFile CreateSourceWithUnusedNames()
    {
        var block = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 64, namePoolCapacity: 4096, rootRow: 5);
        var writer = new BlockWriter(block);
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(5, ".", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 0)));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(6, "Makefile", FileColumns with { ParentRow = 5 }));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(7, "makefile", FileColumns with { ParentRow = 5 }));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(8, "caf\u00E9-old.txt", FileColumns with { ParentRow = 5 }));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(9, "gone.txt", FileColumns with { ParentRow = 5 }));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(10, "freed.txt", new RowColumns(5, RowFlags.None, 0, 0, 0, 0)));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(11, "old-1.txt", FileColumns with { ParentRow = 5 }));
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(12, "pending.txt", FileColumns with { ParentRow = 5 }));
        writer.Complete(Moment, null);
        Assert.AreEqual(WriteRefusal.None, writer.TryRenameRow(11, "old-2.txt", 5));
        Assert.AreEqual(WriteRefusal.None, writer.TryRenameRow(11, "new.txt", 5));
        Assert.AreEqual(WriteRefusal.None, writer.TryRenameRow(6, "GNUmakefile", 5));
        Assert.AreEqual(WriteRefusal.None, writer.TryRenameRow(8, "caf\u00E9.txt", 5));
        Assert.AreEqual(WriteRefusal.None, writer.MarkTombstone(9));
        Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(12, @"T:\pending.txt"));
        return block;
    }

    static string?[] RowNames(BlockFile block) =>
        [.. Enumerable.Range(0, (int)block.RowCount).Select(row =>
            block.Rows[row].NameId == 0 ? null : NamePool.ReadRowName(block, (uint)row).ToString())];

    static string[] StoredNames(BlockFile block) =>
        [.. Enumerable.Range(1, (int)block.NameEntryCount).Select(nameId => NamePool.Read(block, (uint)nameId).ToString())];

    [TestMethod]
    public void CopyCompacting_EveryRowReadsTheSameNameAndUnusedNamesAreGone()
    {
        using var source = CreateSourceWithUnusedNames();
        using var target = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 8192,
            nameEntryCapacity: 4096);
        using var references = NameReferences.Count(source);

        BlockCopier.CopyCompacting(source, target, references, CancellationToken.None);

        CollectionAssert.AreEqual(RowNames(source), RowNames(target));
        var unused = new[] { "Makefile", "caf\u00E9-old.txt", "old-1.txt", "old-2.txt", "pending.txt" };
        CollectionAssert.IsSubsetOf(unused, StoredNames(source));
        Assert.IsFalse(StoredNames(target).Intersect(unused).Any(), "no unused name is stored");
        Assert.AreEqual(references.Usage.ReferencedEntries, target.NameEntryCount);
        Assert.AreEqual(source.NameEntryCount - (uint)unused.Length, target.NameEntryCount);
        Assert.AreEqual(references.Usage.CompactedPoolBytes, target.NamePoolUsed);
        Assert.IsLessThan(source.NamePoolUsed, target.NamePoolUsed);
    }

    [TestMethod]
    public void CopyCompacting_KeepsTheOrderTheFlagsAndAValidPoolChainsAndPostingBase()
    {
        using var source = CreateSourceWithUnusedNames();
        using var target = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 8192,
            nameEntryCapacity: 4096);
        using var references = NameReferences.Count(source);

        BlockCopier.CopyCompacting(source, target, references, CancellationToken.None);

        var survivors = StoredNames(source).Where((_, index) => references.IsReferenced((uint)index + 1)).ToArray();
        CollectionAssert.AreEqual(survivors, StoredNames(target), "ascending old-identifier order");
        for (var row = 0; row < source.RowCount; row++)
        {
            Assert.AreEqual(source.Rows[row].Flags, target.Rows[row].Flags, $"flags {row}");
            Assert.AreEqual(source.Rows[row].ParentRow, target.Rows[row].ParentRow, $"parent {row}");
        }

        Assert.IsNull(NamePoolIntegrity.Check(target));
        Assert.IsNull(PostingIntegrity.Check(target));
        Assert.IsTrue(target.Header.IsComplete);
        Assert.AreEqual(target.NameEntryCount, target.PostingBaseNameCount);
        Assert.AreEqual(0u, target.DeltaEntryCount);
        foreach (var spelling in survivors)
        {
            Assert.IsTrue(NamePool.TryFindOrdinal(target, spelling, out var nameId), spelling);
            var offsets = target.PostingOffsets;
            var postings = target.PostingRows[(int)offsets[(int)nameId]..(int)offsets[(int)nameId + 1]];
            foreach (var row in postings)
            {
                Assert.AreEqual(spelling, NamePool.ReadRowName(target, row).ToString());
            }

            Assert.AreEqual(RowNames(source).Count(name => name == spelling), postings.Length, spelling);
        }
    }

    [TestMethod]
    public void NameReferences_CountsTombstonedFreedAndDeletePendingRowsAsReferences()
    {
        using var source = CreateSourceWithUnusedNames();
        using var references = NameReferences.Count(source);

        foreach (var row in new uint[] { 9, 10, 12 })
        {
            Assert.IsTrue(references.IsReferenced(source.Rows[(int)row].NameId), $"row {row}");
        }

        Assert.AreEqual(source.NameEntryCount, references.Usage.StoredEntries);
        Assert.AreEqual(5u, references.Usage.UnusedEntries);
        Assert.IsFalse(references.IsReferenced(0));
    }

    [TestMethod]
    public void CopyCompacting_RefusesACountOfAnotherBlockOrAnEarlierState()
    {
        using var source = CreateSourceWithUnusedNames();
        using var other = CreateSourceWithUnusedNames();
        using var target = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 8192);
        using var otherReferences = NameReferences.Count(other);
        using var earlier = NameReferences.Count(source);
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(source).TryRenameRow(11, "later.txt", 5));

        Assert.Throws<ArgumentException>(() =>
            BlockCopier.CopyCompacting(source, target, otherReferences, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => BlockCopier.CopyCompacting(source, target, earlier, CancellationToken.None));
    }

    [TestMethod]
    public void CopyCompacting_ObservesCancellationAndLeavesTheTargetIncomplete()
    {
        using var source = CreateSourceWithUnusedNames();
        using var target = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 8192);
        using var references = NameReferences.Count(source);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BlockCopier.CopyCompacting(source, target, references, cancelled.Token));

        Assert.IsFalse(target.Header.IsComplete);
    }

    [TestMethod]
    public void NameReferences_RejectsARowNamingAnEntryPastThePublishedNames()
    {
        using var source = CreateSourceWithUnusedNames();
        FileRow.WriteDescriptorWord(ref source.Rows[7], source.NameEntryCount + 1, RowFlags.InUse);

        Assert.Throws<InvalidDataException>(() => NameReferences.Count(source));
    }

    /// <summary>Enumeration growth copies this way: an unfinished walk keeps every name and identifier.</summary>
    [TestMethod]
    public void Copy_KeepsUnusedNamesAndEveryIdentifierVerbatim()
    {
        using var source = CreateSourceWithUnusedNames();
        using var target = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 8192);

        BlockCopier.Copy(source, target, buildPostings: false, complete: false);

        CollectionAssert.AreEqual(StoredNames(source), StoredNames(target));
        for (var row = 0; row < source.RowCount; row++)
        {
            Assert.AreEqual(source.Rows[row].NameId, target.Rows[row].NameId, $"row {row}");
        }
    }
}
