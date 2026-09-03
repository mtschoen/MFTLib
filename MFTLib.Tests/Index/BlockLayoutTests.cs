using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockLayoutTests
{
    [TestMethod]
    public void Magic_IsMlixLittleEndian()
    {
        var bytes = BitConverter.GetBytes(BlockLayout.Magic);
        Assert.AreEqual((byte)'M', bytes[0]);
        Assert.AreEqual((byte)'L', bytes[1]);
        Assert.AreEqual((byte)'I', bytes[2]);
        Assert.AreEqual((byte)'X', bytes[3]);
    }

    [TestMethod]
    public void AlignUp_RoundsToNextPage()
    {
        Assert.AreEqual(0L, BlockLayout.AlignUp(0, BlockLayout.PageSize));
        Assert.AreEqual(4096L, BlockLayout.AlignUp(1, BlockLayout.PageSize));
        Assert.AreEqual(4096L, BlockLayout.AlignUp(4096, BlockLayout.PageSize));
        Assert.AreEqual(8192L, BlockLayout.AlignUp(4097, BlockLayout.PageSize));
    }

    [TestMethod]
    public void ComputeSlotCapacity_SmallVolume_UsesMinimumHeadroom()
    {
        // 1000 rows: 25 percent would be 250, which is below the 64K floor.
        Assert.AreEqual(1000u + 65536u, BlockLayout.ComputeSlotCapacity(1000));
    }

    [TestMethod]
    public void ComputeSlotCapacity_LargeVolume_UsesTwentyFivePercent()
    {
        Assert.AreEqual(4_000_000u + 1_000_000u, BlockLayout.ComputeSlotCapacity(4_000_000));
    }

    [TestMethod]
    public void ComputeNamePoolCapacity_SmallEstimate_UsesMinimumHeadroom()
    {
        Assert.AreEqual(1024u + 1_048_576u, BlockLayout.ComputeNamePoolCapacity(1024));
    }

    [TestMethod]
    public void NamePoolOffset_IsPageAlignedAfterRows()
    {
        // 100 rows * 32 bytes = 3200, aligned up to 4096, after the 4096 header page.
        Assert.AreEqual(8192L, BlockLayout.NamePoolOffset(100));
        Assert.AreEqual(0L, BlockLayout.NamePoolOffset(100) % BlockLayout.PageSize);
    }

    [TestMethod]
    public void TotalBlockBytes_IsPageAligned()
    {
        var total = BlockLayout.TotalBlockBytes(100, 1000);
        Assert.AreEqual(0L, total % BlockLayout.PageSize);
        Assert.IsTrue(total >= BlockLayout.NamePoolOffset(100) + 1000);
    }

    [TestMethod]
    public void RowFlags_HaveDistinctSingleBitValues()
    {
        Assert.AreEqual((ushort)1, (ushort)RowFlags.InUse);
        Assert.AreEqual((ushort)2, (ushort)RowFlags.Directory);
        Assert.AreEqual((ushort)4, (ushort)RowFlags.Tombstone);
        Assert.AreEqual((ushort)8, (ushort)RowFlags.SizeUnknown);
        Assert.AreEqual((ushort)16, (ushort)RowFlags.SubtreeSkipped);
    }

    [TestMethod]
    public void ProducerKind_MatchesSpecNumbering()
    {
        Assert.AreEqual(1u, (uint)ProducerKind.Mft);
        Assert.AreEqual(2u, (uint)ProducerKind.Enumeration);
    }
}
