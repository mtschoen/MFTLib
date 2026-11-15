using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>MFTLib issue 953: the capacities a watch growth plans for each growable refusal.</summary>
[TestClass]
public sealed class WatchGrowthPlanTests
{
    static readonly BlockCapacities Small = new(SlotCapacity: 64, NameEntryCapacity: 64, NamePoolCapacity: 4096,
        DeltaCapacity: 16);

    [TestMethod]
    public void Slots_PlanFromTheRefusedRecordAndNeverShrinkTheOtherRegions()
    {
        var large = Small with { NameEntryCapacity = 10_000_000, NamePoolCapacity = 1 << 30, DeltaCapacity = 5_000_000 };

        var planned = WatchGrowthPlan.Plan(large, rowCount: 64, WriteRefusal.Slots, recordNumber: 100_000);

        var volumePlan = BlockLayout.PlanCapacities(100_001, default);
        Assert.AreEqual(volumePlan.SlotCapacity, planned.SlotCapacity);
        Assert.AreEqual(large.NameEntryCapacity, planned.NameEntryCapacity);
        Assert.AreEqual(large.NamePoolCapacity, planned.NamePoolCapacity);
        Assert.AreEqual(large.DeltaCapacity, planned.DeltaCapacity);
    }

    [TestMethod]
    public void Slots_ReplanTheOtherRegionsFromTheNewSlotCount()
    {
        var planned = WatchGrowthPlan.Plan(Small, rowCount: 64, WriteRefusal.Slots, recordNumber: 200);

        Assert.AreEqual(BlockLayout.PlanCapacities(201, default), planned);
        Assert.IsGreaterThan(200u, planned.SlotCapacity);
    }

    [TestMethod]
    public void Slots_PastTheLargestPlan_AreAtTheClamp()
    {
        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() =>
            WatchGrowthPlan.Plan(Small, rowCount: 64, WriteRefusal.Slots, recordNumber: uint.MaxValue - 1));

        StringAssert.Contains(refusal.Message, "slot region");
    }

    [TestMethod]
    public void NameEntries_DoubleOnlyTheEntryRegion()
    {
        Assert.AreEqual(Small with { NameEntryCapacity = 128 },
            WatchGrowthPlan.Plan(Small, rowCount: 10, WriteRefusal.NameEntries, recordNumber: 9));
    }

    [TestMethod]
    public void NamePool_DoublesOnlyThePoolRegion()
    {
        Assert.AreEqual(Small with { NamePoolCapacity = 8192 },
            WatchGrowthPlan.Plan(Small, rowCount: 10, WriteRefusal.NamePool, recordNumber: 9));
    }

    [TestMethod]
    public void NameRegionsAtTheirClamps_AreNotGrowable()
    {
        var entries = Assert.ThrowsExactly<InvalidOperationException>(() => WatchGrowthPlan.Plan(
            Small with { NameEntryCapacity = BlockLayout.MaximumNameEntryCapacity }, 10, WriteRefusal.NameEntries, 9));
        var pool = Assert.ThrowsExactly<InvalidOperationException>(() => WatchGrowthPlan.Plan(
            Small with { NamePoolCapacity = BlockLayout.MaximumNamePoolCapacity }, 10, WriteRefusal.NamePool, 9));

        StringAssert.Contains(entries.Message, "name entry region");
        StringAssert.Contains(pool.Message, "name pool region");
    }

    [TestMethod]
    public void Delta_DoublesUntilItReachesTheSlotCount_ThenStays()
    {
        var once = WatchGrowthPlan.Plan(Small, 10, WriteRefusal.Delta, 9);
        var twice = WatchGrowthPlan.Plan(once, 10, WriteRefusal.Delta, 9);
        var capped = WatchGrowthPlan.Plan(Small with { DeltaCapacity = 48 }, 10, WriteRefusal.Delta, 9);
        var stays = WatchGrowthPlan.Plan(Small with { DeltaCapacity = 65536 }, 10, WriteRefusal.Delta, 9);

        Assert.AreEqual(Small with { DeltaCapacity = 32 }, once);
        Assert.AreEqual(Small with { DeltaCapacity = 64 }, twice);
        Assert.AreEqual(64u, capped.DeltaCapacity);
        Assert.AreEqual(65536u, stays.DeltaCapacity);
    }

    [TestMethod]
    public void ARegionThatCannotGrow_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WatchGrowthPlan.Plan(Small, 10, WriteRefusal.None, 9));
    }
}
