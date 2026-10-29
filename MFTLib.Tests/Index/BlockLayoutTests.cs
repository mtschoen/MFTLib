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

    [TestMethod]
    public void PlanCapacities_WithoutAMinimum_IsTheVolumePlan()
    {
        Assert.AreEqual(new BlockCapacities(1250000, 1250000, 30003200),
            BlockLayout.PlanCapacities(1000000, default));
    }

    [TestMethod]
    [DataRow(4000000u, 0u, 0u, 4000000u, 1250000u, 30003200u)]
    [DataRow(0u, 2500000u, 0u, 1250000u, 2500000u, 30003200u)]
    [DataRow(0u, 0u, 60006400u, 1250000u, 1250000u, 60006400u)]
    [DataRow(1000u, 1000u, 4096u, 1250000u, 1250000u, 30003200u)]
    public void PlanCapacities_AMinimumRaisesOnlyItsRegion(uint minimumSlots, uint minimumEntries, uint minimumPool,
        uint slots, uint entries, uint pool)
    {
        var minimum = new BlockCapacities(minimumSlots, minimumEntries, minimumPool);
        Assert.AreEqual(new BlockCapacities(slots, entries, pool),
            BlockLayout.PlanCapacities(1000000, minimum));
    }

    [TestMethod]
    [DataRow(65536u, 65536u)]
    [DataRow(2000000u, 2000000u)]
    [DataRow(2000005u, 2000001u)]
    [DataRow(18912320u, 5382464u)]
    [DataRow(4268800u, 2453760u)]
    public void ComputeNameEntryCapacity_IsFullForSmallSlotCapacitiesAndAFifthBeyond(uint slots, uint entries)
    {
        Assert.AreEqual(entries, BlockLayout.ComputeNameEntryCapacity(slots));
    }

    [TestMethod]
    public void PlanCapacities_ALargeVolumeReservesAFractionOfTheEntryRegionPerSlot()
    {
        var capacities = BlockLayout.PlanCapacities(15129856, default);

        Assert.AreEqual(18912320u, capacities.SlotCapacity);
        Assert.AreEqual(5382464u, capacities.NameEntryCapacity);
        Assert.AreEqual(64589824L, BlockLayout.NameEntryRegionBytes(capacities.NameEntryCapacity));
    }

    [TestMethod]
    public void GrowthMinimum_AnExhaustedRightSizedEntryRegionDoublesOnlyEntries()
    {
        var plan = BlockLayout.PlanCapacities(15129856, default);
        var header = new BlockHeader
        {
            RowCount = 1000,
            SlotCapacity = plan.SlotCapacity,
            NameEntryCapacity = plan.NameEntryCapacity,
            NameEntryCount = plan.NameEntryCapacity,
            NamePoolCapacity = plan.NamePoolCapacity,
            NamePoolUsed = 1000,
            Flags = BlockFlags.CapacityExhausted
        };

        Assert.AreEqual(new BlockCapacities(0, plan.NameEntryCapacity * 2, 0), BlockLayout.GrowthMinimum(in header));
    }

    [TestMethod]
    public void PlanCapacities_AHugeEstimateClampsToACreatableSlotCapacity()
    {
        var capacities = BlockLayout.PlanCapacities(uint.MaxValue, default);
        Assert.AreEqual(int.MaxValue - 1u, capacities.SlotCapacity);
        Assert.AreEqual(0x7FFFF000u, capacities.NamePoolCapacity);
    }

    [TestMethod]
    public void GrowthMinimum_WithoutTheFlag_IsNone()
    {
        var header = new BlockHeader
        {
            RowCount = 64,
            SlotCapacity = 64,
            NameEntryCapacity = 64,
            NamePoolCapacity = 4096
        };
        Assert.AreEqual(default(BlockCapacities), BlockLayout.GrowthMinimum(in header));
    }

    [TestMethod]
    [DataRow("Rows")]
    [DataRow("Entries")]
    [DataRow("Pool")]
    public void GrowthMinimum_DoublesOnlyTheRegionAtCapacity(string region)
    {
        var header = new BlockHeader
        {
            Flags = BlockFlags.CapacityExhausted,
            RowCount = 6,
            SlotCapacity = 64,
            NameEntryCapacity = 64,
            NamePoolCapacity = 4096
        };
        var expected = region switch
        {
            "Rows" => SetRowsAndExpect(ref header, new BlockCapacities(128, 0, 0)),
            "Entries" => SetEntriesAndExpect(ref header, new BlockCapacities(0, 128, 0)),
            "Pool" => SetPoolAndExpect(ref header, new BlockCapacities(0, 0, 8192)),
            _ => throw new ArgumentOutOfRangeException(nameof(region))
        };
        Assert.AreEqual(expected, BlockLayout.GrowthMinimum(in header));
    }

    [TestMethod]
    public void GrowthMinimum_PoolWithMoreThanTheMarginLeft_IsNotExhausted()
    {
        var header = new BlockHeader
        {
            Flags = BlockFlags.CapacityExhausted,
            SlotCapacity = 64,
            NameEntryCount = 64,
            NameEntryCapacity = 64,
            NamePoolUsed = 3585,
            NamePoolCapacity = 4096
        };
        Assert.AreEqual(new BlockCapacities(0, 128, 0), BlockLayout.GrowthMinimum(in header));
    }

    [TestMethod]
    public void GrowthMinimum_FlagWithNoRegionAtCapacity_DoublesRows()
    {
        var header = new BlockHeader
        {
            Flags = BlockFlags.CapacityExhausted,
            RowCount = 6,
            SlotCapacity = 64,
            NameEntryCapacity = 64,
            NamePoolCapacity = 4096
        };
        Assert.AreEqual(new BlockCapacities(128, 0, 0), BlockLayout.GrowthMinimum(in header));
    }

    [TestMethod]
    public void GrowthMinimum_EntriesAndPoolTogether_DoublesBoth()
    {
        var header = new BlockHeader
        {
            Flags = BlockFlags.CapacityExhausted,
            SlotCapacity = 64,
            NameEntryCount = 64,
            NameEntryCapacity = 64,
            NamePoolUsed = 3586,
            NamePoolCapacity = 4096
        };
        Assert.AreEqual(new BlockCapacities(0, 128, 8192), BlockLayout.GrowthMinimum(in header));
    }

    [TestMethod]
    public void GrowthMinimum_AtTheClamp_ReturnsTheClampedValue()
    {
        var header = new BlockHeader
        {
            Flags = BlockFlags.CapacityExhausted,
            RowCount = int.MaxValue - 1u,
            SlotCapacity = int.MaxValue - 1u,
            NameEntryCapacity = 64,
            NamePoolCapacity = 4096
        };
        Assert.AreEqual(new BlockCapacities(int.MaxValue - 1u, 0, 0), BlockLayout.GrowthMinimum(in header));
    }

    static BlockCapacities SetRowsAndExpect(ref BlockHeader header, BlockCapacities expected)
    {
        header.RowCount = header.SlotCapacity;
        return expected;
    }

    static BlockCapacities SetEntriesAndExpect(ref BlockHeader header, BlockCapacities expected)
    {
        header.NameEntryCount = header.NameEntryCapacity;
        return expected;
    }

    static BlockCapacities SetPoolAndExpect(ref BlockHeader header, BlockCapacities expected)
    {
        header.NamePoolUsed = header.NamePoolCapacity - BlockLayout.NamePoolExhaustionMarginBytes;
        return expected;
    }
}
