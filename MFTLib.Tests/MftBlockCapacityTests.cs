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

        var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation);

        Assert.AreEqual(BlockLayout.ComputeSlotCapacity(4_000_000), slotCapacity);
        Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(slotCapacity), namePoolCapacity);
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
        Assert.AreEqual(uint.MaxValue / 2, MftBlockCapacity.EstimateRowCount(volumeInformation));
    }

    [TestMethod]
    public void Plan_RecordCountBeyondThirtyTwoBits_SurvivesTheSlotCapacityHeadroom()
    {
        // The clamp exists so an extreme volume degrades gracefully. Clamping to
        // uint.MaxValue instead selected the one input whose quarter headroom overflows
        // ComputeSlotCapacity's checked addition.
        var volumeInformation = new NtfsVolumeInformation(long.MaxValue, 1024);
        var (slotCapacity, _) = MftBlockCapacity.Plan(volumeInformation);
        Assert.AreEqual(BlockLayout.ComputeSlotCapacity(uint.MaxValue / 2), slotCapacity);
    }

    [TestMethod]
    public void Plan_HugeVolume_ClampsThePoolBelowTwoGibibytes()
    {
        // 24 bytes per slot for this volume exceeds what a pool span length can address.
        var volumeInformation = new NtfsVolumeInformation(100_000_000L * 1024, 1024);
        var (_, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation);
        Assert.AreEqual(BlockLayout.MaximumNamePoolCapacity, namePoolCapacity);
    }
}
