using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class JournalCheckpointLossTests
{
    [TestMethod]
    [DataRow(12288L, 4096L)]
    [DataRow(long.MaxValue, 1L)]
    public void TryGetGrowthTarget_ProjectsRecordedSizeAndAllocationDelta(long retainedSize, long allocationDelta)
    {
        var loss = new JournalCheckpointLoss('T', JournalCheckpointLossDetection.ScanCatchUp,
            JournalCheckpointLossCause.CheckpointTrimmed,
            new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = allocationDelta })
        {
            SizeThatWouldHaveRetained = retainedSize
        };

        Assert.IsTrue(loss.TryGetGrowthTarget(out var target));
        Assert.AreEqual(retainedSize, target.MaximumSize);
        Assert.AreEqual(allocationDelta, target.AllocationDelta);
    }

    [TestMethod]
    [DataRow(JournalCheckpointLossCause.JournalRecreated, 12288L, 4096L, 4096L)]
    [DataRow(JournalCheckpointLossCause.JournalAdvanced, 12288L, 4096L, 4096L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, null, 4096L, 4096L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 4096L, 4096L, 4096L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 2048L, 4096L, 4096L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 0L, -1L, 4096L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, -1L, -2L, 4096L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 12288L, 4096L, 0L)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 12288L, 4096L, -1L)]
    public void TryGetGrowthTarget_IneligibleReportReturnsDefault(JournalCheckpointLossCause cause,
        long? retainedSize, long recordedMaximum, long allocationDelta)
    {
        var loss = new JournalCheckpointLoss('T', JournalCheckpointLossDetection.ScanCatchUp, cause,
            new UsnJournalSettings { MaximumSize = recordedMaximum, AllocationDelta = allocationDelta })
        {
            SizeThatWouldHaveRetained = retainedSize
        };

        Assert.IsFalse(loss.TryGetGrowthTarget(out var target));
        Assert.AreEqual(default(UsnJournalSettings), target);
    }
}
