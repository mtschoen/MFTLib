using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class BlockWriterTests
{
    static RowColumns Columns(ushort sequence) => new(0, RowFlags.InUse, 0, 10, 0, sequence);

    [TestMethod]
    [DataRow((ushort)9, (ushort)9, DisplayName = "live slot, same sequence")]
    [DataRow((ushort)9, (ushort)12, DisplayName = "live slot, higher sequence")]
    [DataRow(ushort.MaxValue, (ushort)1, DisplayName = "live slot, real wrap")]
    public void TryWriteRow_StoresTheSuppliedSequenceExactly(ushort seeded, ushort supplied)
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(3, "first.txt", Columns(seeded)));

        Assert.IsTrue(writer.TryWriteRow(3, "second.txt", Columns(supplied)));

        Assert.AreEqual(supplied, block.SequenceNumbers[3]);
    }

    [TestMethod]
    [DataRow((ushort)14, (ushort)15, DisplayName = "the next sequence")]
    [DataRow((ushort)14, (ushort)14, DisplayName = "the same sequence, as a freed record imported by a scan")]
    [DataRow(ushort.MaxValue, (ushort)1, DisplayName = "a real wrap")]
    public void TryWriteRow_OverATombstonedSlot_StoresTheSuppliedSequenceExactly(ushort seeded, ushort supplied)
    {
        using var builder = new SyntheticBlockBuilder();
        using var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(3, "first.txt", Columns(seeded)));
        writer.MarkTombstone(3);

        Assert.IsTrue(writer.TryWriteRow(3, "second.txt", Columns(supplied)));

        Assert.AreEqual(supplied, block.SequenceNumbers[3]);
    }
}
