using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockWriterTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    static readonly RowColumns RootColumns = new(
        ParentRow: 0,
        Flags: RowFlags.InUse | RowFlags.Directory,
        Attributes: 16,
        Size: 0,
        ModifiedTicks: Moment.Ticks);

    string _directory = null!;
    string _blockPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _blockPath = Path.Combine(_directory, "T-0BADF00D.mlix");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a just-unmapped file briefly; a leftover temp directory is harmless.
        }
    }

    static RowColumns FileColumns(long size = 0, uint attributes = 0, uint parentRow = 0)
    {
        return new RowColumns(parentRow, RowFlags.InUse, attributes, size, Moment.Ticks);
    }

    BlockFile CreateBlock(uint slotCapacity = 64, uint namePoolCapacity = 512)
    {
        return BlockFile.Create(new BlockFileCreateOptions
        {
            Path = _blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity
        });
    }

    [TestMethod]
    public void TryWriteRow_WritesRowAndNameAndAdvancesRowCount()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);

        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        Assert.IsTrue(writer.TryWriteRow(1, "report.pdf", FileColumns(size: 4096, attributes: 32)));

        Assert.AreEqual(2u, writer.RowCount);
        Assert.AreEqual("report.pdf", new string(NamePool.ReadRowName(block, 1)));
        Assert.AreEqual(4096L, block.Rows[1].Size);
        Assert.AreEqual(0u, block.Rows[1].ParentRow);
    }

    [TestMethod]
    public void TryWriteRow_PastSlotCapacity_SetsCompactionNeededAndReturnsFalse()
    {
        using var block = CreateBlock(slotCapacity: 4);
        var writer = new BlockWriter(block);

        Assert.IsFalse(writer.TryWriteRow(4, "overflow.txt", FileColumns()));
        Assert.IsTrue(writer.CompactionNeeded);
        Assert.AreEqual(0u, writer.RowCount);
    }

    [TestMethod]
    public void TryWriteRow_WithAnExhaustedNamePool_SetsCompactionNeededAndLeavesTheRowUntouched()
    {
        using var block = CreateBlock(namePoolCapacity: 8);
        var writer = new BlockWriter(block);

        Assert.IsTrue(writer.TryWriteRow(0, "abcd", FileColumns()));
        Assert.IsFalse(writer.TryWriteRow(1, "this name does not fit", FileColumns()));

        Assert.IsTrue(writer.CompactionNeeded);
        Assert.IsFalse(block.Rows[1].IsInUse);
    }

    [TestMethod]
    public void TryWriteRow_KeepsApplyingAfterAFailure()
    {
        using var block = CreateBlock(slotCapacity: 8);
        var writer = new BlockWriter(block);

        Assert.IsFalse(writer.TryWriteRow(99, "far.txt", FileColumns()));
        Assert.IsTrue(writer.TryWriteRow(1, "near.txt", FileColumns()));

        Assert.AreEqual("near.txt", new string(NamePool.ReadRowName(block, 1)));
        Assert.IsTrue(writer.CompactionNeeded);
    }

    [TestMethod]
    public void TryWriteRow_OverATombstonedRowInsideThePublishedRange_PublishesTheDescriptorWordLast()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "gone.tmp", FileColumns(size: 10, attributes: 32));
        writer.MarkTombstone(1);

        // Row 1 is inside the published range (RowCount already covers it), so this create
        // reuses a live slot rather than filling a fresh one, exactly like a journal create
        // over a reused MFT record number.
        Assert.IsTrue(writer.TryWriteRow(1, "reused.txt", FileColumns(size: 20, attributes: 32)));

        var descriptor = FileRow.ReadDescriptorWord(in block.Rows[1]);
        var offsetBytes = FileRow.DescriptorNameOffsetBytes(descriptor);
        var lengthUnits = FileRow.DescriptorNameLengthUnits(descriptor);

        Assert.AreEqual((ushort)"reused.txt".Length, lengthUnits);
        Assert.AreEqual("reused.txt",
            new string(NamePool.Read(block.NamePoolCharacters, offsetBytes, lengthUnits)));
        Assert.AreEqual("reused.txt", new string(NamePool.ReadRowName(block, 1)));
        Assert.AreEqual(RowFlags.InUse, FileRow.DescriptorFlags(descriptor));
        Assert.IsFalse(block.Rows[1].IsDeleted);
    }

    [TestMethod]
    public void TryRenameRow_WithTheSameName_UpdatesOnlyTheParentRowAndDoesNotGrowTheNamePool()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "Documents", RootColumns);
        writer.TryWriteRow(2, "unchanged.txt", FileColumns(size: 10, attributes: 32, parentRow: 0));
        var namePoolUsedBefore = block.Header.NamePoolUsed;

        Assert.IsTrue(writer.TryRenameRow(2, "unchanged.txt", parentRow: 1));

        Assert.AreEqual(namePoolUsedBefore, block.Header.NamePoolUsed);
        Assert.AreEqual(1u, block.Rows[2].ParentRow);
        Assert.AreEqual("unchanged.txt", new string(NamePool.ReadRowName(block, 2)));
    }

    [TestMethod]
    public void TryRenameRow_AppendsTheNewNameAndSwapsTheRowOffset()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "before.txt", FileColumns(size: 10, attributes: 32));
        var originalOffset = block.Rows[1].NameOffsetBytes;

        Assert.IsTrue(writer.TryRenameRow(1, "after.txt", parentRow: 0));

        Assert.AreEqual("after.txt", new string(NamePool.ReadRowName(block, 1)));
        Assert.AreNotEqual(originalOffset, block.Rows[1].NameOffsetBytes);
        Assert.AreEqual("before.txt", new string(NamePool.Read(block.NamePoolCharacters, originalOffset, 10)));
    }

    [TestMethod]
    public void TryRenameRow_PublishesOffsetLengthAndFlagsAsOneDescriptorWord()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "before.txt", FileColumns(size: 10, attributes: 32));
        var originalOffset = block.Rows[1].NameOffsetBytes;

        Assert.IsTrue(writer.TryRenameRow(1, "considerably-longer-name.txt", parentRow: 0));

        // The whole point of the descriptor word: one read yields an offset and a length that
        // belong to the same name. Decoding them separately from the word must agree with the
        // name the pool reader hands back, and the old name must still be readable at the old
        // offset because the pool is append-only.
        var descriptor = FileRow.ReadDescriptorWord(in block.Rows[1]);
        var offsetBytes = FileRow.DescriptorNameOffsetBytes(descriptor);
        var lengthUnits = FileRow.DescriptorNameLengthUnits(descriptor);

        Assert.AreNotEqual(originalOffset, offsetBytes);
        Assert.AreEqual((ushort)"considerably-longer-name.txt".Length, lengthUnits);
        Assert.AreEqual("considerably-longer-name.txt",
            new string(NamePool.Read(block.NamePoolCharacters, offsetBytes, lengthUnits)));
        Assert.AreEqual("considerably-longer-name.txt", new string(NamePool.ReadRowName(block, 1)));
        Assert.AreEqual(RowFlags.InUse, FileRow.DescriptorFlags(descriptor));
        Assert.AreEqual("before.txt",
            new string(NamePool.Read(block.NamePoolCharacters, originalOffset, 10)));
    }

    [TestMethod]
    public void MarkTombstone_AfterARename_KeepsTheRenamedNameIntact()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "before.txt", FileColumns(size: 10, attributes: 32));
        Assert.IsTrue(writer.TryRenameRow(1, "after.txt", parentRow: 0));

        writer.MarkTombstone(1);

        // A flags update rewrites the whole descriptor word, so it must carry the renamed
        // name's offset and length through untouched rather than resetting them.
        Assert.IsTrue(block.Rows[1].IsDeleted);
        Assert.IsTrue(block.Rows[1].IsInUse);
        Assert.AreEqual("after.txt", new string(NamePool.ReadRowName(block, 1)));
    }

    [TestMethod]
    public void MarkSubtreeSkipped_SetsTheFlagAndKeepsTheName()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "Locked", FileColumns(attributes: 16));

        writer.MarkSubtreeSkipped(1);

        Assert.IsTrue(block.Rows[1].SubtreeSkipped);
        Assert.AreEqual("Locked", new string(NamePool.ReadRowName(block, 1)));
    }

    [TestMethod]
    public void MarkSubtreeSkipped_PastSlotCapacity_SetsCompactionNeeded()
    {
        using var block = CreateBlock(slotCapacity: 4);
        var writer = new BlockWriter(block);

        writer.MarkSubtreeSkipped(9);

        Assert.IsTrue(writer.CompactionNeeded);
    }

    [TestMethod]
    public void MarkTombstone_KeepsTheNameAndSetsTheDeletedFlag()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "gone.tmp", FileColumns(size: 10, attributes: 32));

        writer.MarkTombstone(1);

        Assert.IsTrue(block.Rows[1].IsDeleted);
        Assert.AreEqual("gone.tmp", new string(NamePool.ReadRowName(block, 1)));
    }

    [TestMethod]
    public void Complete_SetsTheCompleteFlagAndTheScanTimestampLast()
    {
        using (var block = CreateBlock())
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "", RootColumns);
            Assert.IsFalse(block.Header.IsComplete);
            writer.Complete(Moment);
            Assert.IsTrue(block.Header.IsComplete);
            Assert.AreEqual(Moment, block.Header.ScanTimestampUtc);
        }

        using var reopened = BlockFile.Open(_blockPath, 0x0BADF00D, out var validation);
        Assert.AreEqual(BlockValidationResult.Valid, validation);
        Assert.IsNotNull(reopened);
    }

    [TestMethod]
    public void SetJournalCursor_AndBumpGeneration_WriteIntoTheHeader()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);

        writer.SetJournalCursor(0xABCD, 5000);
        Assert.AreEqual(0xABCDul, block.Header.UsnJournalId);
        Assert.AreEqual(5000L, block.Header.UsnNextUsn);

        Assert.AreEqual(1ul, writer.BumpGeneration());
        Assert.AreEqual(2ul, writer.BumpGeneration());
        Assert.AreEqual(2ul, block.Header.Generation);
    }

    [TestMethod]
    public void MarkCompactionNeeded_IsVisibleInTheHeaderFlags()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.MarkCompactionNeeded();
        Assert.IsTrue(block.Header.IsCompactionNeeded);
    }
}
