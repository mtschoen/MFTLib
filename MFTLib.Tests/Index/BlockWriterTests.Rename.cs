using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class BlockWriterTests
{
    [TestMethod]
    public void TryRenameRow_WithTheSameName_UpdatesOnlyTheParentRowAndDoesNotGrowTheNamePool()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "Documents", RootColumns);
        writer.TryWriteRow(2, "unchanged.txt", FileColumns(0, size: 10, attributes: 32, parentRow: 0));
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
        writer.TryWriteRow(1, "before.txt", FileColumns(0, size: 10, attributes: 32));
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
        writer.TryWriteRow(1, "before.txt", FileColumns(0, size: 10, attributes: 32));
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
    public void TryRenameRow_RowIndexAtOrPastSlotCapacity_FlagsCompactionAndFails()
    {
        using var block = CreateBlock(slotCapacity: 4);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);

        Assert.IsFalse(writer.TryRenameRow(4, "overflow.txt", parentRow: 0));

        Assert.IsTrue(block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public void TryRenameRow_NamePoolExhausted_FailsWithoutChangingTheRow()
    {
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = _blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = 64,
            NamePoolCapacity = 40
        });
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        // Fills the tiny name pool (40 bytes = 20 UTF-16 code units) with no room left over.
        writer.TryWriteRow(1, "twenty-characters-ok", FileColumns(0, size: 10, attributes: 32));
        var originalOffset = block.Rows[1].NameOffsetBytes;

        Assert.IsFalse(writer.TryRenameRow(1, "this-new-name-does-not-fit", parentRow: 0));

        Assert.AreEqual(originalOffset, block.Rows[1].NameOffsetBytes);
        Assert.AreEqual("twenty-characters-ok", new string(NamePool.ReadRowName(block, 1)));
    }
}
