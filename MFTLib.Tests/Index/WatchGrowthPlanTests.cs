using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: the capacities a watch growth plans for each growable refusal, and when its copy compacts
///     names. A refused name region doubles only when it is still more than half full after compaction.
/// </summary>
[TestClass]
public sealed class WatchGrowthPlanTests
{
    static readonly BlockCapacities Small = new(SlotCapacity: 64, NameEntryCapacity: 64, NamePoolCapacity: 4096,
        DeltaCapacity: 16);

    /// <summary>Every stored name referenced: compaction drops nothing.</summary>
    static readonly NameUsage AllReferenced = new(StoredEntries: 64, ReferencedEntries: 64, StoredPoolBytes: 4096,
        CompactedPoolBytes: 4096);

    static BlockCapacities Capacities(BlockCapacities current, WriteRefusal reason, NameUsage? names = null,
        uint rowCount = 10, uint recordNumber = 9) =>
        WatchGrowthPlan.Plan(current, rowCount, reason, recordNumber, names ?? AllReferenced).Capacities;

    [TestMethod]
    public void Slots_PlanFromTheRefusedRecordAndNeverShrinkTheOtherRegions()
    {
        var large = Small with { NameEntryCapacity = 10_000_000, NamePoolCapacity = 1 << 30, DeltaCapacity = 5_000_000 };

        var planned = Capacities(large, WriteRefusal.Slots, rowCount: 64, recordNumber: 100_000);

        var volumePlan = BlockLayout.PlanCapacities(100_001, default);
        Assert.AreEqual(volumePlan.SlotCapacity, planned.SlotCapacity);
        Assert.AreEqual(large.NameEntryCapacity, planned.NameEntryCapacity);
        Assert.AreEqual(large.NamePoolCapacity, planned.NamePoolCapacity);
        Assert.AreEqual(large.DeltaCapacity, planned.DeltaCapacity);
    }

    [TestMethod]
    public void Slots_ReplanTheOtherRegionsFromTheNewSlotCount()
    {
        var planned = Capacities(Small, WriteRefusal.Slots, rowCount: 64, recordNumber: 200);

        Assert.AreEqual(BlockLayout.PlanCapacities(201, default), planned);
        Assert.IsGreaterThan(200u, planned.SlotCapacity);
    }

    [TestMethod]
    public void Slots_PastTheLargestPlan_AreAtTheClamp()
    {
        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Capacities(Small, WriteRefusal.Slots, rowCount: 64, recordNumber: uint.MaxValue - 1));

        StringAssert.Contains(refusal.Message, "slot region");
    }

    [TestMethod]
    public void NameEntries_StillMoreThanHalfFullAfterCompaction_DoubleOnlyTheEntryRegion()
    {
        Assert.AreEqual(Small with { NameEntryCapacity = 128 }, Capacities(Small, WriteRefusal.NameEntries,
            AllReferenced with { ReferencedEntries = 33 }));
    }

    [TestMethod]
    public void NameEntries_AtMostHalfFullAfterCompaction_KeepTheirCapacity()
    {
        Assert.AreEqual(Small, Capacities(Small, WriteRefusal.NameEntries, AllReferenced with { ReferencedEntries = 32 }));
    }

    [TestMethod]
    public void NamePool_StillMoreThanHalfFullAfterCompaction_DoublesOnlyThePoolRegion()
    {
        Assert.AreEqual(Small with { NamePoolCapacity = 8192 }, Capacities(Small, WriteRefusal.NamePool,
            AllReferenced with { CompactedPoolBytes = 2049 }));
    }

    [TestMethod]
    public void NamePool_AtMostHalfFullAfterCompaction_KeepsItsCapacity()
    {
        Assert.AreEqual(Small, Capacities(Small, WriteRefusal.NamePool, AllReferenced with { CompactedPoolBytes = 2048 }));
    }

    /// <summary>At its clamp a name region cannot double; the refusal is final only when the referenced names leave no room.</summary>
    [TestMethod]
    public void NameRegionsAtTheirClamps_AreNotGrowable_WhenTheReferencedNamesLeaveNoRoom()
    {
        var entryClamp = BlockLayout.MaximumNameEntryCapacity;
        var poolClamp = BlockLayout.MaximumNamePoolCapacity;
        var entries = Assert.ThrowsExactly<InvalidOperationException>(() => Capacities(
            Small with { NameEntryCapacity = entryClamp }, WriteRefusal.NameEntries,
            AllReferenced with { StoredEntries = entryClamp, ReferencedEntries = entryClamp }));
        var pool = Assert.ThrowsExactly<InvalidOperationException>(() => Capacities(
            Small with { NamePoolCapacity = poolClamp }, WriteRefusal.NamePool,
            AllReferenced with { CompactedPoolBytes = poolClamp - BlockLayout.NamePoolExhaustionMarginBytes }));

        StringAssert.Contains(entries.Message, "name entry region");
        StringAssert.Contains(entries.Message, "referenced names alone leave no room");
        StringAssert.Contains(pool.Message, "name pool region");
    }

    [TestMethod]
    public void NameRegionsAtTheirClamps_KeepTheirCapacity_WhenCompactionLeavesRoom()
    {
        var entryClamp = BlockLayout.MaximumNameEntryCapacity;
        var poolClamp = BlockLayout.MaximumNamePoolCapacity;
        var atEntryClamp = Small with { NameEntryCapacity = entryClamp };
        var atPoolClamp = Small with { NamePoolCapacity = poolClamp };

        Assert.AreEqual(atEntryClamp, Capacities(atEntryClamp, WriteRefusal.NameEntries,
            AllReferenced with { StoredEntries = entryClamp, ReferencedEntries = entryClamp - 1 }));
        Assert.AreEqual(atPoolClamp, Capacities(atPoolClamp, WriteRefusal.NamePool,
            AllReferenced with { CompactedPoolBytes = poolClamp - BlockLayout.NamePoolExhaustionMarginBytes - 1 }));
    }

    [TestMethod]
    public void Delta_DoublesUntilItReachesTheSlotCount_ThenStays()
    {
        var once = Capacities(Small, WriteRefusal.Delta);
        var twice = Capacities(once, WriteRefusal.Delta);
        var capped = Capacities(Small with { DeltaCapacity = 48 }, WriteRefusal.Delta);
        var stays = Capacities(Small with { DeltaCapacity = 65536 }, WriteRefusal.Delta);

        Assert.AreEqual(Small with { DeltaCapacity = 32 }, once);
        Assert.AreEqual(Small with { DeltaCapacity = 64 }, twice);
        Assert.AreEqual(64u, capped.DeltaCapacity);
        Assert.AreEqual(65536u, stays.DeltaCapacity);
    }

    [TestMethod]
    public void ARegionThatCannotGrow_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Capacities(Small, WriteRefusal.None));
    }

    [TestMethod]
    [DataRow(nameof(WriteRefusal.NameEntries))]
    [DataRow(nameof(WriteRefusal.NamePool))]
    public void ANameRegionGrowth_AlwaysCompacts(string reason)
    {
        Assert.IsTrue(WatchGrowthPlan.Plan(Small, 10, Enum.Parse<WriteRefusal>(reason), 9, AllReferenced).CompactsNames);
    }

    /// <summary>A slot or delta growth compacts once unused names are at least one eighth of the stored ones.</summary>
    [TestMethod]
    [DataRow(nameof(WriteRefusal.Delta), 8u, true, DisplayName = "Delta, one eighth unused")]
    [DataRow(nameof(WriteRefusal.Delta), 7u, false, DisplayName = "Delta, below one eighth")]
    [DataRow(nameof(WriteRefusal.Slots), 8u, true, DisplayName = "Slots, one eighth unused")]
    [DataRow(nameof(WriteRefusal.Slots), 7u, false, DisplayName = "Slots, below one eighth")]
    [DataRow(nameof(WriteRefusal.Delta), 0u, false, DisplayName = "Delta, nothing unused")]
    public void AnotherGrowth_CompactsAtTheOneEighthThreshold(string reason, uint unused, bool compacts)
    {
        var names = AllReferenced with { ReferencedEntries = AllReferenced.StoredEntries - unused };

        Assert.AreEqual(compacts, WatchGrowthPlan.Plan(Small, 10, Enum.Parse<WriteRefusal>(reason), 9, names).CompactsNames);
    }
}
