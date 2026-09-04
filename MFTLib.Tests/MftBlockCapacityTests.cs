using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftBlockCapacityTests
{
    [TestMethod]
    public void Plan_LargeVolume_UsesTheRecordCountAndTheAverageNameLength()
    {
        // 4 million records at 1024 bytes per file record segment.
        var volumeInformation = new NtfsVolumeInformation(
            MftValidDataLength: 4_000_000L * 1024, BytesPerFileRecordSegment: 1024,
            BytesPerSector: 512, BytesPerCluster: 4096, TotalClusters: 0, FreeClusters: 0);

        var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation);

        Assert.AreEqual(BlockLayout.ComputeSlotCapacity(4_000_000), slotCapacity);
        Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(slotCapacity * 48u), namePoolCapacity);
    }

    [TestMethod]
    public void EstimateRowCount_NullVolumeInformation_FallsBackToTheMinimum()
    {
        Assert.AreEqual(MftBlockCapacity.MinimumEstimatedRowCount, MftBlockCapacity.EstimateRowCount(null));
    }

    [TestMethod]
    public void EstimateRowCount_UnqueriedSegmentSize_FallsBackToTheMinimum()
    {
        // BytesPerFileRecordSegment zero is the type's documented unqueried case, so
        // MftRecordCount is zero and there is nothing to size from.
        var volumeInformation = new NtfsVolumeInformation(1024, 0, 0, 0, 0, 0);
        Assert.AreEqual(MftBlockCapacity.MinimumEstimatedRowCount,
            MftBlockCapacity.EstimateRowCount(volumeInformation));
    }

    [TestMethod]
    public void Plan_HonorsACallerSuppliedAverageNameLength()
    {
        var volumeInformation = new NtfsVolumeInformation(1_000_000L * 1024, 1024, 0, 0, 0, 0);
        var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation, 96);
        Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(slotCapacity * 96u), namePoolCapacity);
    }

    [TestMethod]
    public void EstimateRowCount_RecordCountBeyondThirtyTwoBits_Clamps()
    {
        var volumeInformation = new NtfsVolumeInformation(long.MaxValue, 1024, 0, 0, 0, 0);
        Assert.AreEqual(uint.MaxValue, MftBlockCapacity.EstimateRowCount(volumeInformation));
    }

    [TestMethod]
    public void Plan_ZeroAverageNameBytesPerRow_ThrowsArgumentOutOfRangeException()
    {
        var volumeInformation = new NtfsVolumeInformation(1_000_000L * 1024, 1024, 0, 0, 0, 0);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => MftBlockCapacity.Plan(volumeInformation, 0));
    }

    [TestMethod]
    public void Plan_HugeNamePoolEstimate_ClampsToPreventOverflow()
    {
        // Large volume with high average name bytes per row that would exceed uint.MaxValue / 2
        var volumeInformation = new NtfsVolumeInformation(100_000_000L * 1024, 1024, 0, 0, 0, 0);
        var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation, 1000);
        Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(uint.MaxValue / 2), namePoolCapacity);
    }
}
