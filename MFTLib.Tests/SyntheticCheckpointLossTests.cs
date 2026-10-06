using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="SyntheticCheckpointLoss" /> sets the raw journal positions a
///     <see cref="JournalCheckpointLoss" /> keeps internal. Report equality covers them, so a fixture that
///     compares a scripted loss with the report an index publishes has to be able to match them exactly.
/// </summary>
[TestClass]
public class SyntheticCheckpointLossTests
{
    static JournalCheckpointLoss PositionLess() => new JournalCheckpointLoss('T', JournalCheckpointLossDetection.LiveWatch, JournalCheckpointLossCause.CheckpointTrimmed,
        4096, 32768)
    {
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    [TestMethod]
    public void WithJournalPositions_SetsThePositionsAndKeepsEveryPublicValue()
    {
        var original = PositionLess();

        var copy = SyntheticCheckpointLoss.WithJournalPositions(original, 1000, 5000, 9000);

        Assert.AreEqual(1000L, copy.CheckpointUsn);
        Assert.AreEqual(5000L, copy.FirstUsn);
        Assert.AreEqual(9000L, copy.NextUsn);
        Assert.AreEqual('T', copy.DriveLetter);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, copy.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, copy.Cause);
        Assert.AreEqual(4096L, copy.AllocationDelta);
        Assert.AreEqual(32768L, copy.MaximumSize);
        Assert.AreEqual(4000L, copy.BytesBehind, "derived sizes are not recomputed from the positions");
        Assert.AreEqual(12288L, copy.SizeThatWouldHaveRetained);
        Assert.AreEqual(0L, original.CheckpointUsn, "the original is not changed");
    }

    [TestMethod]
    public void WithJournalPositions_MatchesAReportBuiltWithThoseExactPositions()
    {
        var produced = WatchDeduplicationTestSupport.StandardCatchUpLoss('T');
        var scripted = SyntheticCheckpointLoss.WithJournalPositions(
            produced with { CheckpointUsn = 0, FirstUsn = 0, NextUsn = 0 },
            produced.CheckpointUsn, produced.FirstUsn, produced.NextUsn);

        Assert.AreEqual(produced, scripted);
        Assert.AreNotEqual(
            produced,
            SyntheticCheckpointLoss.WithJournalPositions(
                scripted, produced.CheckpointUsn, produced.FirstUsn, produced.NextUsn + 1),
            "report equality covers the hidden positions");
    }

    [TestMethod]
    public void WithJournalPositions_OfNoReport_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => SyntheticCheckpointLoss.WithJournalPositions(null!, 1, 2, 3));
    }
}
