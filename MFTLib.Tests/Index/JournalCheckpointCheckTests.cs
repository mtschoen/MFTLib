using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Which cached checkpoints <see cref="JournalCheckpointCheck" /> calls unresumable, and
///     what it reports about them. The journal read is swapped out through
///     <c>JournalCheckpointCheck._journalOverride</c>, so these run on every platform.
/// </summary>
[TestClass]
[DoNotParallelize]
public class JournalCheckpointCheckTests
{
    const ulong JournalId = 0xABCD;

    static IDisposable Journal(ulong journalId = JournalId, long firstUsn = 1_000, long nextUsn = 5_000,
        long maximumSize = 128L * 1024 * 1024, long allocationDelta = 64)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(
            _ => new JournalWindow(journalId, firstUsn, nextUsn, allocationDelta, maximumSize));
    }

    [TestMethod]
    public void CheckpointStillInTheJournal_IsResumable()
    {
        using var journal = Journal(firstUsn: 1_000, nextUsn: 5_000);

        // At the oldest retained USN the checkpoint is still readable, and past it plainly so.
        Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 1_000));
        Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 4_000));
        Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 5_000));
    }

    [TestMethod]
    public void CheckpointTrimmedOutOfTheSameJournal_ReportsTheSizeThatWouldHaveKeptIt()
    {
        using var journal = Journal(firstUsn: 1_000, nextUsn: 5_000, allocationDelta: 64);

        var loss = JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 900);

        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('C', loss.DriveLetter);
        Assert.AreEqual(900L, loss.CheckpointUsn);
        Assert.AreEqual(1_000L, loss.FirstUsn);
        Assert.AreEqual(5_000L, loss.NextUsn);
        Assert.AreEqual(64L, loss.AllocationDelta);
        Assert.AreEqual(128L * 1024 * 1024, loss.MaximumSize);
        // 1000 - 900 trimmed away, and 5000 - 900 rounded up to a multiple of 64 keeps it.
        Assert.AreEqual(100L, loss.BytesBehind);
        Assert.AreEqual(4_160L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public void JournalRecreated_SuggestsNoSizeBecauseNoSizeWouldHaveHelped()
    {
        using var journal = Journal(journalId: 0xFEED, firstUsn: 0, nextUsn: 200);

        // The checkpoint names a record in a journal instance that no longer exists, so its
        // USN and this journal's USNs are not comparable at all.
        var loss = JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 900);

        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.AreEqual(900L, loss.CheckpointUsn);
        Assert.IsNull(loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    /// <summary>
    ///     A recreated journal is reported as recreated even when the checkpoint happens to
    ///     sit inside the new journal's window: the id, not the arithmetic, decides.
    /// </summary>
    [TestMethod]
    public void JournalRecreated_OutranksACheckpointThatLooksInRange()
    {
        using var journal = Journal(journalId: 0xFEED, firstUsn: 0, nextUsn: 5_000);

        var loss = JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 900);

        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public void VolumeThatCannotAnswer_ReportsNothing()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);

        Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 0));
    }

    /// <summary>
    ///     Incoherent journal metadata is not evidence against the cached block, so it reports
    ///     nothing rather than forcing a rescan on numbers it cannot trust.
    /// </summary>
    [TestMethod]
    public void IncoherentJournalMetadata_ReportsNothing()
    {
        using (Journal(firstUsn: -1))
        {
            Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 10));
        }

        // A window that runs backwards.
        using (Journal(firstUsn: 5_000, nextUsn: 1_000))
        {
            Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 10));
        }

        // No allocation unit to round a suggested size to.
        using (Journal(allocationDelta: 0))
        {
            Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 10));
        }

        // A negative checkpoint is a corrupt block header, not a very old checkpoint.
        using (Journal())
        {
            Assert.IsNull(JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: -1));
        }
    }

    [TestMethod]
    public void RealisticJournalNumbers_RoundToWholeAllocationUnits()
    {
        const long megabyte = 1024 * 1024;
        using var journal = Journal(
            firstUsn: 1_000_000_000,
            nextUsn: 1_000_000_000 + 300 * megabyte,
            maximumSize: 128 * megabyte,
            allocationDelta: 64 * megabyte);

        // A checkpoint 20 MB before the oldest retained record: 320 MB of journal keeps it.
        var loss = JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 1_000_000_000 - 20 * megabyte);

        Assert.IsNotNull(loss);
        Assert.AreEqual(20 * megabyte, loss.BytesBehind);
        Assert.AreEqual(320 * megabyte, loss.SizeThatWouldHaveRetained);
        Assert.IsTrue(loss.SizeThatWouldHaveRetained > loss.MaximumSize,
            "the hint is only worth showing when it asks for a bigger journal than the current one");
    }

    [TestMethod]
    public void TrimmedCheckpoint_UnrepresentableRetentionSize_PreservesLossWithoutHint()
    {
        using var journal = Journal(firstUsn: 1, nextUsn: long.MaxValue, allocationDelta: 64);

        var loss = JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 0);

        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('C', loss.DriveLetter);
        Assert.AreEqual(0L, loss.CheckpointUsn);
        Assert.AreEqual(1L, loss.FirstUsn);
        Assert.AreEqual(long.MaxValue, loss.NextUsn);
        Assert.AreEqual(64L, loss.AllocationDelta);
        Assert.AreEqual(128L * 1024 * 1024, loss.MaximumSize);
        Assert.AreEqual(1L, loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public void TrimmedCheckpoint_LargestAlignedRetentionSize_RemainsExact()
    {
        const long largestAlignedSize = long.MaxValue - 63;
        using var journal = Journal(firstUsn: 1, nextUsn: largestAlignedSize, allocationDelta: 64);

        var loss = JournalCheckpointCheck.Check('C', JournalId, checkpointUsn: 0);

        Assert.IsNotNull(loss);
        Assert.AreEqual(largestAlignedSize, loss.SizeThatWouldHaveRetained);
    }
}
