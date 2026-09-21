using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The one piece of arithmetic behind the rescan hint. USNs are byte offsets into the
///     journal, so the size that would have retained a checkpoint is exact integer
///     arithmetic over them: no clock, no rate, no estimate.
/// </summary>
[TestClass]
public class JournalSizeArithmeticTests
{
    [TestMethod]
    public void SizeThatWouldHaveRetained_IsTheSpanRoundedUpToTheAllocationDelta()
    {
        // 100 bytes behind the tip, in 64-byte allocation units, needs two units.
        Assert.AreEqual(128L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 900, nextUsn: 1_000, allocationDelta: 64));
        // An exact multiple is not rounded up to the next unit.
        Assert.AreEqual(128L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 872, nextUsn: 1_000, allocationDelta: 64));
        // One byte over a unit boundary takes a whole further unit.
        Assert.AreEqual(192L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 871, nextUsn: 1_000, allocationDelta: 64));
        // A delta of one leaves the span untouched.
        Assert.AreEqual(100L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 900, nextUsn: 1_000, allocationDelta: 1));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_RealisticJournalNumbers()
    {
        // A checkpoint 300 MB behind the tip of a journal whose allocation delta is 64 MB:
        // five 64 MB units cover it, four do not.
        const long megabyte = 1024 * 1024;
        Assert.AreEqual(320 * megabyte,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(
                checkpointUsn: 1_000_000_000, nextUsn: 1_000_000_000 + 300 * megabyte,
                allocationDelta: 64 * megabyte));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_CheckpointAtOrBeyondTheTip_NeedsNothing()
    {
        // The checkpoint was not behind at all, so no journal size is implied by it.
        Assert.AreEqual(0L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 1_000, nextUsn: 1_000, allocationDelta: 64));
        Assert.AreEqual(0L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 1_001, nextUsn: 1_000, allocationDelta: 64));
        Assert.AreEqual(0L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: long.MaxValue, nextUsn: 0, allocationDelta: 64));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_NegativeUsns_AreRejected()
    {
        // USNs are byte offsets, so a negative one is a corrupt input, not a small one.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: -1, nextUsn: 1_000, allocationDelta: 64));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 0, nextUsn: -1, allocationDelta: 64));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_NonPositiveAllocationDelta_IsRejected()
    {
        // Rounding up to a unit of zero has no answer, and a negative unit has no meaning.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 0, nextUsn: 1_000, allocationDelta: 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 0, nextUsn: 1_000, allocationDelta: -64));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_RoundingPastTheRangeOfTheResult_Overflows()
    {
        // The span fits, but rounding it up to the next allocation unit does not.
        Assert.ThrowsException<OverflowException>(() =>
            JournalSizeArithmetic.SizeThatWouldHaveRetained(
                checkpointUsn: 0, nextUsn: long.MaxValue, allocationDelta: 64));

        // The largest span that still rounds cleanly is returned rather than refused.
        const long span = long.MaxValue - (long.MaxValue % 64);
        Assert.AreEqual(span,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 0, nextUsn: span, allocationDelta: 64));
    }

    [TestMethod]
    public void BytesBehind_IsTheGapBetweenTheCheckpointAndTheOldestRetainedRecord()
    {
        Assert.AreEqual(400L, JournalSizeArithmetic.BytesBehind(checkpointUsn: 600, firstUsn: 1_000));
        // A checkpoint still inside the journal is not behind.
        Assert.AreEqual(0L, JournalSizeArithmetic.BytesBehind(checkpointUsn: 1_000, firstUsn: 1_000));
        Assert.AreEqual(0L, JournalSizeArithmetic.BytesBehind(checkpointUsn: 1_400, firstUsn: 1_000));
    }

    [TestMethod]
    public void BytesBehind_NegativeUsns_AreRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            JournalSizeArithmetic.BytesBehind(checkpointUsn: -1, firstUsn: 1_000));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            JournalSizeArithmetic.BytesBehind(checkpointUsn: 0, firstUsn: -1));
    }
}
