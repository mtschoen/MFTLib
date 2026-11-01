using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class BlockWriterTests
{
    [TestMethod]
    public void TryWriteRowUnderAccess_RefusesReturnedScopeBeforeTouchingRows()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        BlockAccessScope access;
        using (access = block.TakeAccess()) { }

        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.TryWriteRowUnderAccess(access, 1, "late", FileColumns(1)));
        Assert.AreEqual(0u, block.RowCount);
        Assert.AreEqual(0u, block.NameEntryCount);
    }

    [TestMethod]
    public void TryWriteRowUnderAccess_RefusesAnotherBlocksScopeBeforeTouchingRows()
    {
        using var block = TestBlocks.Create(_blockPath);
        using var other = TestBlocks.Create(Path.Combine(_directory.Path, "other.mlix"));
        var writer = new BlockWriter(block);
        using var access = other.TakeAccess();

        Assert.ThrowsExactly<ArgumentException>(() => writer.TryWriteRowUnderAccess(access, 1, "foreign", FileColumns(1)));
        Assert.AreEqual(0u, block.RowCount);
        Assert.AreEqual(0u, block.NameEntryCount);
    }
}
