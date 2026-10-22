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
    public void DoubleCapacity_DoublesAndClampsBelowTheOverflowOfLaterHeadroomArithmetic()
    {
        Assert.AreEqual(8192u, BlockLayout.DoubleCapacity(4096));
        Assert.AreEqual(uint.MaxValue / 2, BlockLayout.DoubleCapacity(uint.MaxValue - 1));
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
        // 100 rows * 32 bytes = 3200, aligned up to 4096, then plus a
        // 100 * 2-byte sequence region aligned to 4096.
        Assert.AreEqual(12288L, BlockLayout.NamePoolOffset(100));
        Assert.AreEqual(0L, BlockLayout.NamePoolOffset(100) % BlockLayout.PageSize);
    }

    [TestMethod]
    public void SequenceRegion_SitsBetweenTheRowRegionAndTheNamePool()
    {
        const uint slotCapacity = 1000;
        var sequenceOffset = BlockLayout.SequenceRegionOffset(slotCapacity);
        Assert.AreEqual(BlockLayout.RowRegionOffset + BlockLayout.RowRegionBytes(slotCapacity), sequenceOffset);
        Assert.AreEqual(0L, sequenceOffset % BlockLayout.PageSize);
        Assert.AreEqual(sequenceOffset + BlockLayout.SequenceRegionBytes(slotCapacity),
            BlockLayout.NamePoolOffset(slotCapacity));
    }

    [TestMethod]
    public void SequenceRegion_IsTwoBytesPerSlotRoundedToAPage()
    {
        Assert.AreEqual(BlockLayout.PageSize, BlockLayout.SequenceRegionBytes(1));
        Assert.AreEqual(12288, BlockLayout.SequenceRegionBytes(4097));
    }

    [TestMethod]
    public void TotalBlockBytes_IsPageAligned()
    {
        var total = BlockLayout.TotalBlockBytes(100, 1000);
        Assert.AreEqual(0L, total % BlockLayout.PageSize);
        Assert.IsGreaterThanOrEqualTo(BlockLayout.NamePoolOffset(100) + 1000, total);
    }

    [TestMethod]
    public void RowFlags_HaveDistinctSingleBitValues()
    {
        var inUse = (ushort)RowFlags.InUse;
        var directory = (ushort)RowFlags.Directory;
        var tombstone = (ushort)RowFlags.Tombstone;
        var sizeUnknown = (ushort)RowFlags.SizeUnknown;
        var subtreeSkipped = (ushort)RowFlags.SubtreeSkipped;

        Assert.AreEqual((ushort)1, inUse);
        Assert.AreEqual((ushort)2, directory);
        Assert.AreEqual((ushort)4, tombstone);
        Assert.AreEqual((ushort)8, sizeUnknown);
        Assert.AreEqual((ushort)16, subtreeSkipped);
    }

    [TestMethod]
    public void ProducerKind_MatchesSpecNumbering()
    {
        Assert.AreEqual(1u, (uint)ProducerKind.Mft);
        Assert.AreEqual(2u, (uint)ProducerKind.Enumeration);
    }
}
