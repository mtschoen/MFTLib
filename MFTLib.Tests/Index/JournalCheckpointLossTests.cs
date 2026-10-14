using MFTLib.Index;
using MFTLibTestExtensions;
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
        Assert.IsNull(loss.GrowthTargetRefusalReason);
        Assert.AreEqual(retainedSize, target.MaximumSize);
        Assert.AreEqual(allocationDelta, target.AllocationDelta);
    }

    [TestMethod]
    [DataRow(JournalCheckpointLossCause.JournalRecreated, 12288L, 4096L, 4096L, JournalGrowthTargetRefusalReason.CheckpointNotTrimmed)]
    [DataRow(JournalCheckpointLossCause.JournalAdvanced, 12288L, 4096L, 4096L, JournalGrowthTargetRefusalReason.CheckpointNotTrimmed)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, null, 4096L, 4096L, JournalGrowthTargetRefusalReason.RetentionSizeUnavailable)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 4096L, 4096L, 4096L, JournalGrowthTargetRefusalReason.RetentionSizeNotGreaterThanMaximum)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 2048L, 4096L, 4096L, JournalGrowthTargetRefusalReason.RetentionSizeNotGreaterThanMaximum)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 0L, -1L, 4096L, JournalGrowthTargetRefusalReason.NonPositiveRetentionSize)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, -1L, -2L, 4096L, JournalGrowthTargetRefusalReason.NonPositiveRetentionSize)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 12288L, 4096L, 0L, JournalGrowthTargetRefusalReason.NonPositiveAllocationDelta)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 12288L, 4096L, -1L, JournalGrowthTargetRefusalReason.NonPositiveAllocationDelta)]
    [DataRow(JournalCheckpointLossCause.JournalRecreated, null, 4096L, 0L, JournalGrowthTargetRefusalReason.CheckpointNotTrimmed)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, null, 4096L, 0L, JournalGrowthTargetRefusalReason.RetentionSizeUnavailable)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 0L, 4096L, 0L, JournalGrowthTargetRefusalReason.NonPositiveRetentionSize)]
    [DataRow(JournalCheckpointLossCause.CheckpointTrimmed, 2048L, 4096L, 0L, JournalGrowthTargetRefusalReason.NonPositiveAllocationDelta)]
    public void TryGetGrowthTarget_IneligibleReportReturnsDefault(JournalCheckpointLossCause cause,
        long? retainedSize, long recordedMaximum, long allocationDelta, JournalGrowthTargetRefusalReason expectedReason)
    {
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('T', cause,
            JournalCheckpointLossDetection.ScanCatchUp,
            new UsnJournalSettings { MaximumSize = recordedMaximum, AllocationDelta = allocationDelta }) with
        {
            SizeThatWouldHaveRetained = retainedSize
        };

        Assert.IsFalse(loss.TryGetGrowthTarget(out var target));
        Assert.AreEqual(default(UsnJournalSettings), target);
        Assert.AreEqual(expectedReason, loss.GrowthTargetRefusalReason);
    }

    [TestMethod]
    public void GrowthTargetRefusalReason_WithCopiesReflectCurrentValuesWithoutChangingOriginal()
    {
        var original = SyntheticNotifications.CreateJournalCheckpointLoss('T', JournalCheckpointLossCause.CheckpointTrimmed,
            JournalCheckpointLossDetection.ScanCatchUp,
            new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 4096 });
        Assert.AreEqual(JournalGrowthTargetRefusalReason.RetentionSizeUnavailable, original.GrowthTargetRefusalReason);

        var eligible = original with { SizeThatWouldHaveRetained = 12288 };
        Assert.IsNull(eligible.GrowthTargetRefusalReason);
        Assert.IsTrue(eligible.TryGetGrowthTarget(out var target));
        Assert.AreEqual(12288L, target.MaximumSize);
        Assert.AreEqual(4096L, target.AllocationDelta);

        var refused = eligible with { JournalSettings = new UsnJournalSettings { MaximumSize = 12288, AllocationDelta = 4096 } };
        Assert.AreEqual(JournalGrowthTargetRefusalReason.RetentionSizeNotGreaterThanMaximum, refused.GrowthTargetRefusalReason);
        Assert.IsFalse(refused.TryGetGrowthTarget(out target));
        Assert.AreEqual(default(UsnJournalSettings), target);
        Assert.IsNull(eligible.GrowthTargetRefusalReason);
        Assert.AreEqual(JournalGrowthTargetRefusalReason.RetentionSizeUnavailable, original.GrowthTargetRefusalReason);
    }
}
