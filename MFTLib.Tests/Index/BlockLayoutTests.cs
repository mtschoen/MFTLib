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
    public void DoubleCapacity_DoublesAndLeavesRoomForReservedNameEntryZero()
    {
        Assert.AreEqual(8192u, BlockLayout.DoubleCapacity(4096));
        Assert.AreEqual((uint)int.MaxValue - 1, BlockLayout.DoubleCapacity(uint.MaxValue - 1));
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
    [DataRow(0u, 1048576u)]
    [DataRow(43690u, 1048576u)]
    [DataRow(43691u, 1052672u)]
    [DataRow(1000000u, 24002560u)]
    [DataRow(2147483646u, 0x7FFFF000u)]
    public void ComputeNamePoolCapacity_ReservesTwentyFourBytesPerSlotWithAOneMebibyteFloor(uint slots, uint expected)
    {
        Assert.AreEqual(expected, BlockLayout.ComputeNamePoolCapacity(slots));
    }

    [TestMethod]
    [DataRow(0u, 1024u)]
    [DataRow(4096u, 1024u)]
    [DataRow(4097u, 2048u)]
    [DataRow(2147483646u, 536870912u)]
    public void ComputeBucketCount_IsAPowerOfTwoAtOrAboveAQuarterOfTheEntries(uint entries, uint expected)
    {
        Assert.AreEqual(expected, BlockLayout.ComputeBucketCount(entries));
    }

    [TestMethod]
    public void DoubleNamePoolCapacity_ClampsBelowTwoGibibytes()
    {
        Assert.AreEqual(2097152u, BlockLayout.DoubleNamePoolCapacity(1048576));
        Assert.AreEqual(BlockLayout.MaximumNamePoolCapacity, BlockLayout.DoubleNamePoolCapacity(0x7FFFF000));
    }

    [TestMethod]
    public void Layout_AtTheLargestSlotCapacity_DoesNotOverflow()
    {
        const uint slots = int.MaxValue - 2;
        var total = BlockLayout.TotalBlockBytes(slots, slots, BlockLayout.ComputeNamePoolCapacity(slots));
        Assert.IsGreaterThan(0L, total);
        Assert.AreEqual(0L, total % BlockLayout.PageSize);
        Assert.IsGreaterThan(0u, BlockLayout.ComputeBucketCount(slots));
    }

    [TestMethod]
    public void NamePoolOffset_IsPageAlignedAfterRows()
    {
        // 100 rows * 32 bytes = 3200 -> 4096; 100 * 2-byte sequences -> 4096; 101 * 12-byte entries -> 4096;
        // 1024 four-byte buckets -> 4096; plus the header page.
        Assert.AreEqual(20480L, BlockLayout.NamePoolOffset(100, 100));
        Assert.AreEqual(0L, BlockLayout.NamePoolOffset(100, 100) % BlockLayout.PageSize);
    }

    [TestMethod]
    public void NameRegions_FollowTheRowsAndSequencesInOrder()
    {
        const uint slotCapacity = 1000;
        var sequenceOffset = BlockLayout.SequenceRegionOffset(slotCapacity);
        Assert.AreEqual(BlockLayout.RowRegionOffset + BlockLayout.RowRegionBytes(slotCapacity), sequenceOffset);
        Assert.AreEqual(0L, sequenceOffset % BlockLayout.PageSize);
        Assert.AreEqual(sequenceOffset + BlockLayout.SequenceRegionBytes(slotCapacity),
            BlockLayout.NameEntryRegionOffset(slotCapacity));
        Assert.AreEqual(BlockLayout.NameEntryRegionOffset(slotCapacity) + BlockLayout.NameEntryRegionBytes(slotCapacity),
            BlockLayout.BucketRegionOffset(slotCapacity, slotCapacity));
        Assert.AreEqual(BlockLayout.BucketRegionOffset(slotCapacity, slotCapacity) + BlockLayout.BucketRegionBytes(slotCapacity),
            BlockLayout.NamePoolOffset(slotCapacity, slotCapacity));
    }

    [TestMethod]
    public void TotalBlockBytes_DependsOnTheEntryCapacityIndependently()
    {
        Assert.IsGreaterThan(BlockLayout.TotalBlockBytes(1000, 1000, 4096),
            BlockLayout.TotalBlockBytes(1000, 2000, 4096));
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
        var total = BlockLayout.TotalBlockBytes(100, 100, 1000);
        Assert.AreEqual(0L, total % BlockLayout.PageSize);
        Assert.IsGreaterThanOrEqualTo(BlockLayout.NamePoolOffset(100, 100) + 1000, total);
    }

    [TestMethod]
    [DataRow((ushort)RowFlags.InUse, (ushort)1)]
    [DataRow((ushort)RowFlags.Directory, (ushort)2)]
    [DataRow((ushort)RowFlags.Tombstone, (ushort)4)]
    [DataRow((ushort)RowFlags.SizeUnknown, (ushort)8)]
    [DataRow((ushort)RowFlags.SubtreeSkipped, (ushort)16)]
    public void RowFlags_HaveDistinctSingleBitValues(ushort flag, ushort expected)
    {
        Assert.AreEqual(expected, flag);
    }

    [TestMethod]
    public void ProducerKind_MatchesSpecNumbering()
    {
        Assert.AreEqual(1u, (uint)ProducerKind.Mft);
        Assert.AreEqual(2u, (uint)ProducerKind.Enumeration);
    }
}
