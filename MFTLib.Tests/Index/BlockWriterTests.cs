using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed partial class BlockWriterTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    static readonly RowColumns RootColumns = new(
        ParentRow: 0,
        Flags: RowFlags.InUse | RowFlags.Directory,
        Attributes: 16,
        Size: 0,
        ModifiedTicks: Moment.Ticks,
        SequenceNumber: 0);

    TempDirectory _directory = null!;
    string _blockPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-writer");
        _blockPath = Path.Combine(_directory.Path, "T-0BADF00D.mlix");
    }

    public void Dispose() => _directory.Dispose();

    static RowColumns FileColumns(ushort sequenceNumber, long size = 0, uint attributes = 0,
        uint parentRow = 0)
    {
        return new RowColumns(parentRow, RowFlags.InUse, attributes, size, Moment.Ticks, sequenceNumber);
    }

    [TestMethod]
    public void TryWriteRow_WritesRowAndNameAndAdvancesRowCount()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);

        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        Assert.IsTrue(writer.TryWriteRow(1, "report.pdf", FileColumns(0, size: 4096, attributes: 32)));

        Assert.AreEqual(2u, writer.RowCount);
        Assert.AreEqual("report.pdf", NamePool.ReadRowName(block, 1).ToString());
        Assert.AreEqual(4096L, block.Rows[1].Size);
        Assert.AreEqual(0u, block.Rows[1].ParentRow);
    }

    [TestMethod]
    public void LiveRowCount_CountsLiveRowsNotSlots()
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        var columns = new RowColumns(0, RowFlags.InUse, 0, 10, 0, 0);

        Assert.IsTrue(writer.TryWriteRow(0, "a.txt", in columns));
        Assert.IsTrue(writer.TryWriteRow(7, "b.txt", in columns));

        Assert.AreEqual(8u, block.Header.RowCount);
        Assert.AreEqual(2u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void LiveRowCount_DropsOnTombstoneAndDoesNotDoubleCount()
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        var columns = new RowColumns(0, RowFlags.InUse, 0, 10, 0, 0);
        writer.TryWriteRow(3, "a.txt", in columns);

        writer.MarkTombstone(3);
        Assert.AreEqual(0u, block.Header.LiveRowCount);

        writer.MarkTombstone(3);
        Assert.AreEqual(0u, block.Header.LiveRowCount);

        Assert.IsTrue(writer.TryWriteRow(3, "reused.txt", in columns));
        Assert.AreEqual(1u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void LiveRowCount_TracksEveryTryWriteRowTransition()
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        var liveColumns = new RowColumns(0, RowFlags.InUse, 0, 10, 0, 0);
        var nonliveColumns = new RowColumns(0, RowFlags.InUse | RowFlags.Tombstone, 0, 10, 0, 0);

        Assert.IsTrue(writer.TryWriteRow(0, "live.txt", in liveColumns));
        Assert.AreEqual(1u, block.Header.LiveRowCount);

        Assert.IsTrue(writer.TryWriteRow(0, "still-live.txt", in liveColumns));
        Assert.AreEqual(1u, block.Header.LiveRowCount);

        Assert.IsTrue(writer.TryWriteRow(1, "deleted.txt", in nonliveColumns));
        Assert.AreEqual(1u, block.Header.LiveRowCount);

        Assert.IsTrue(writer.TryWriteRow(0, "now-deleted.txt", in nonliveColumns));
        Assert.AreEqual(0u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void LiveRowCount_IgnoresFlagsAddedToANonliveRow()
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        var columns = new RowColumns(0, RowFlags.None, 0, 10, 0, 0);
        Assert.IsTrue(writer.TryWriteRow(0, "unused.txt", in columns));

        writer.MarkSubtreeSkipped(0);
        writer.MarkTombstone(0);

        Assert.AreEqual(0u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void TryWriteRow_PastSlotCapacity_SetsCapacityExhaustedAndReturnsFalse()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 4);
        var writer = new BlockWriter(block);

        Assert.IsFalse(writer.TryWriteRow(4, "overflow.txt", FileColumns(0)));
        Assert.IsTrue(writer.CapacityExhausted);
        Assert.AreEqual(0u, writer.RowCount);
        Assert.AreEqual(0u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void TryWriteRow_WithAnExhaustedNamePool_SetsCapacityExhaustedAndLeavesTheRowUntouched()
    {
        using var block = TestBlocks.Create(_blockPath, namePoolCapacity: 8);
        var writer = new BlockWriter(block);

        Assert.IsTrue(writer.TryWriteRow(0, "abcd", FileColumns(0)));
        Assert.IsFalse(writer.TryWriteRow(1, "this name does not fit", FileColumns(0)));

        Assert.IsTrue(writer.CapacityExhausted);
        Assert.IsFalse(block.Rows[1].IsInUse);
        Assert.AreEqual(1u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void TryWriteRow_KeepsApplyingAfterAFailure()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 8);
        var writer = new BlockWriter(block);

        Assert.IsFalse(writer.TryWriteRow(99, "far.txt", FileColumns(0)));
        Assert.IsTrue(writer.TryWriteRow(1, "near.txt", FileColumns(0)));

        Assert.AreEqual("near.txt", NamePool.ReadRowName(block, 1).ToString());
        Assert.IsTrue(writer.CapacityExhausted);
    }

    [TestMethod]
    public void TryWriteRow_OverATombstonedRowInsideThePublishedRange_PublishesTheDescriptorWordLast()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "gone.tmp", FileColumns(0, size: 10, attributes: 32));
        writer.MarkTombstone(1);

        // Row 1 is inside the published range (RowCount already covers it), so this create
        // reuses a live slot rather than filling a fresh one, exactly like a journal create
        // over a reused MFT record number.
        Assert.IsTrue(writer.TryWriteRow(1, "reused.txt", FileColumns(0, size: 20, attributes: 32)));

        var descriptor = FileRow.ReadDescriptorWord(in block.Rows[1]);
        var nameId = FileRow.DescriptorNameId(descriptor);

        Assert.AreEqual("reused.txt",
            NamePool.Read(block, nameId).ToString());
        Assert.AreEqual("reused.txt", NamePool.ReadRowName(block, 1).ToString());
        Assert.AreEqual(RowFlags.InUse, FileRow.DescriptorFlags(descriptor));
        Assert.IsFalse(block.Rows[1].IsDeleted);
    }

    [TestMethod]
    public void TryWriteRow_StoresTheSequenceNumberBesideTheRow()
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);

        Assert.IsTrue(writer.TryWriteRow(5, "root", new RowColumns(
            5, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 42)));

        Assert.AreEqual((ushort)42, block.SequenceNumbers[5]);
        Assert.AreEqual((ushort)0, block.SequenceNumbers[4]);
    }

    [TestMethod]
    public void MarkTombstone_AfterARename_KeepsTheRenamedNameIntact()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "before.txt", FileColumns(0, size: 10, attributes: 32));
        Assert.IsTrue(writer.TryRenameRow(1, "after.txt", parentRow: 0));

        writer.MarkTombstone(1);

        // A flags update rewrites the whole descriptor word, so it must carry the renamed
        // name's offset and length through untouched rather than resetting them.
        Assert.IsTrue(block.Rows[1].IsDeleted);
        Assert.IsTrue(block.Rows[1].IsInUse);
        Assert.AreEqual("after.txt", NamePool.ReadRowName(block, 1).ToString());
    }

    [TestMethod]
    public void MarkSubtreeSkipped_SetsTheFlagAndKeepsTheName()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "Locked", FileColumns(0, attributes: 16));

        writer.MarkSubtreeSkipped(1);

        Assert.IsTrue(block.Rows[1].SubtreeSkipped);
        Assert.AreEqual("Locked", NamePool.ReadRowName(block, 1).ToString());
        Assert.AreEqual(2u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void MarkSubtreeSkipped_PastSlotCapacity_SetsCapacityExhausted()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 4);
        var writer = new BlockWriter(block);

        writer.MarkSubtreeSkipped(9);

        Assert.IsTrue(writer.CapacityExhausted);
    }

    [TestMethod]
    public void MarkTombstone_KeepsTheNameAndSetsTheDeletedFlag()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "gone.tmp", FileColumns(0, size: 10, attributes: 32));

        writer.MarkTombstone(1);

        Assert.IsTrue(block.Rows[1].IsDeleted);
        Assert.AreEqual("gone.tmp", NamePool.ReadRowName(block, 1).ToString());
    }

    [TestMethod]
    public void Complete_SetsTheCompleteFlagAndTheScanTimestampLast()
    {
        using (var block = TestBlocks.Create(_blockPath))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "", RootColumns);
            Assert.IsFalse(block.Header.IsComplete);
            writer.Complete(Moment, null);
            Assert.IsTrue(block.Header.IsComplete);
            Assert.AreEqual(Moment, block.Header.ScanTimestampUtc);
        }

        using var reopened = BlockFile.Open(_blockPath, 0x0BADF00D, out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
    }

    [TestMethod]
    public void SetJournalCursor_AndBumpGeneration_WriteIntoTheHeader()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);

        writer.SetJournalCursor(0xABCD, 5000);
        Assert.AreEqual(0xABCDul, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(5000L, block.Header.UsnNextUsn);

        Assert.AreEqual(1ul, writer.BumpGeneration());
        Assert.AreEqual(2ul, writer.BumpGeneration());
        Assert.AreEqual(2ul, block.Header.Generation);
    }

    [TestMethod]
    public void MarkCapacityExhausted_IsVisibleInTheHeaderFlags()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.MarkCapacityExhausted();
        Assert.IsTrue(block.Header.IsCapacityExhausted);
    }
}
