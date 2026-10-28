using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class BlockWriterTests
{
    [TestMethod]
    public void TryRenameRow_CaseOnly_InternsTheNewSpellingAndSwapsTheId()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "readme.md", FileColumns(0));
        var originalNameId = block.Rows[1].NameId;
        var originalEntryCount = block.NameEntryCount;

        Assert.IsTrue(writer.TryRenameRow(1, "README.md", parentRow: 0));

        Assert.AreNotEqual(originalNameId, block.Rows[1].NameId);
        Assert.AreEqual(originalEntryCount + 1, block.NameEntryCount);
        Assert.AreEqual("README.md", NamePool.ReadRowName(block, 1).ToString());
        Assert.IsNull(MFTLibTestExtensions.SyntheticNamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryRenameRow_PeersSharingTheOldNameKeepIt()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(10, "notes.txt", FileColumns(0));
        writer.TryWriteRow(11, "notes.txt", FileColumns(0));
        var sharedNameId = block.Rows[11].NameId;
        Assert.AreEqual(block.Rows[10].NameId, sharedNameId);

        Assert.IsTrue(writer.TryRenameRow(10, "Notes.txt", parentRow: 0));

        Assert.AreEqual("Notes.txt", NamePool.ReadRowName(block, 10).ToString());
        Assert.AreEqual("notes.txt", NamePool.ReadRowName(block, 11).ToString());
        Assert.AreEqual(sharedNameId, block.Rows[11].NameId);
        Assert.IsNull(MFTLibTestExtensions.SyntheticNamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryRenameRow_WithTheSameName_UpdatesOnlyTheParentRowAndDoesNotGrowTheNamePool()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "Documents", RootColumns);
        writer.TryWriteRow(2, "unchanged.txt", FileColumns(0, size: 10, attributes: 32, parentRow: 0));
        var namePoolUsedBefore = block.Header.NamePoolUsed;

        Assert.IsTrue(writer.TryRenameRow(2, "unchanged.txt", parentRow: 1));

        Assert.AreEqual(namePoolUsedBefore, block.Header.NamePoolUsed);
        Assert.AreEqual(1u, block.Rows[2].ParentRow);
        Assert.AreEqual("unchanged.txt", NamePool.ReadRowName(block, 2).ToString());
    }

    [TestMethod]
    public void TryRenameRow_AppendsTheNewNameAndSwapsTheRowId()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "before.txt", FileColumns(0, size: 10, attributes: 32));
        var originalNameId = block.Rows[1].NameId;

        Assert.IsTrue(writer.TryRenameRow(1, "after.txt", parentRow: 0));

        Assert.AreEqual("after.txt", NamePool.ReadRowName(block, 1).ToString());
        Assert.AreNotEqual(originalNameId, block.Rows[1].NameId);
        Assert.AreEqual("before.txt", NamePool.Read(block, originalNameId).ToString());
    }

    [TestMethod]
    public void TryRenameRow_PublishesNameIdAndFlagsAsOneDescriptorWord()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        writer.TryWriteRow(1, "before.txt", FileColumns(0, size: 10, attributes: 32));
        var originalNameId = block.Rows[1].NameId;

        Assert.IsTrue(writer.TryRenameRow(1, "considerably-longer-name.txt", parentRow: 0));

        // The published descriptor identifies immutable name storage, including after a rename.
        var descriptor = FileRow.ReadDescriptorWord(in block.Rows[1]);
        var nameId = FileRow.DescriptorNameId(descriptor);

        Assert.AreNotEqual(originalNameId, nameId);
        Assert.AreEqual("considerably-longer-name.txt",
            NamePool.Read(block, nameId).ToString());
        Assert.AreEqual("considerably-longer-name.txt", NamePool.ReadRowName(block, 1).ToString());
        Assert.AreEqual(RowFlags.InUse, FileRow.DescriptorFlags(descriptor));
        Assert.AreEqual("before.txt",
            NamePool.Read(block, originalNameId).ToString());
    }

    [TestMethod]
    public void TryRenameRow_RowIndexAtOrPastSlotCapacity_FlagsCapacityExhaustionAndFails()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 4);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);

        Assert.IsFalse(writer.TryRenameRow(4, "overflow.txt", parentRow: 0));

        Assert.IsTrue(block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void TryRenameRow_NamePoolExhausted_FailsWithoutChangingTheRow()
    {
        using var block = TestBlocks.Create(_blockPath, namePoolCapacity: 20);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        // ASCII storage leaves one byte unused, too little for the replacement.
        writer.TryWriteRow(1, "twenty-characters-ok", FileColumns(0, size: 10, attributes: 32));
        var originalNameId = block.Rows[1].NameId;

        Assert.IsFalse(writer.TryRenameRow(1, "this-new-name-does-not-fit", parentRow: 0));

        Assert.AreEqual(originalNameId, block.Rows[1].NameId);
        Assert.AreEqual("twenty-characters-ok", NamePool.ReadRowName(block, 1).ToString());
    }
}
