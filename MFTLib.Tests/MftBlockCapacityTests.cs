using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftBlockCapacityTests
{
    [TestMethod]
    public void Plan_LargeVolume_UsesTheRecordCountAndTheSharedPoolReservation()
    {
        // 4 million records at 1024 bytes per file record segment.
        var volumeInformation = new NtfsVolumeInformation(
            MftValidDataLength: 4_000_000L * 1024, BytesPerFileRecordSegment: 1024);

        var capacities = MftBlockCapacity.Plan(volumeInformation, default);

        Assert.AreEqual(BlockLayout.ComputeSlotCapacity(4_000_000), capacities.SlotCapacity);
        Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(capacities.SlotCapacity), capacities.NamePoolCapacity);
    }

    [TestMethod]
    public void Plan_AMinimumRaisesOnlyItsRegion()
    {
        var volumeInformation = new NtfsVolumeInformation(1_000_000L * 1024, 1024);

        var capacities = MftBlockCapacity.Plan(volumeInformation, new BlockCapacities(0, 2_500_000, 0));

        Assert.AreEqual(new BlockCapacities(1_250_000, 2_500_000, 30_003_200), capacities);
    }

    [TestMethod]
    public void EstimateRowCount_UnqueriedSegmentSize_FallsBackToTheMinimum()
    {
        // BytesPerFileRecordSegment zero is the type's documented unqueried case, so
        // MftRecordCount is zero and there is nothing to size from.
        var volumeInformation = new NtfsVolumeInformation(1024, 0);
        Assert.AreEqual(MftBlockCapacity.MinimumEstimatedRowCount,
            MftBlockCapacity.EstimateRowCount(volumeInformation));
    }

    [TestMethod]
    public void EstimateRowCount_RecordCountBeyondThirtyTwoBits_ClampsBelowTheSlotHeadroom()
    {
        var volumeInformation = new NtfsVolumeInformation(long.MaxValue, 1024);
        Assert.AreEqual(1717986917u, MftBlockCapacity.EstimateRowCount(volumeInformation));
    }

    [TestMethod]
    public void Plan_RecordCountBeyondThirtyTwoBits_SurvivesTheSlotCapacityHeadroom()
    {
        var volumeInformation = new NtfsVolumeInformation(long.MaxValue, 1024);
        var capacities = MftBlockCapacity.Plan(volumeInformation, default);
        Assert.AreEqual(int.MaxValue - 1u, capacities.SlotCapacity);
    }

    [TestMethod]
    public void Plan_HugeVolume_ClampsThePoolBelowTwoGibibytes()
    {
        // 24 bytes per slot for this volume exceeds what a pool span length can address.
        var volumeInformation = new NtfsVolumeInformation(100_000_000L * 1024, 1024);
        var capacities = MftBlockCapacity.Plan(volumeInformation, default);
        Assert.AreEqual(BlockLayout.MaximumNamePoolCapacity, capacities.NamePoolCapacity);
    }
}
