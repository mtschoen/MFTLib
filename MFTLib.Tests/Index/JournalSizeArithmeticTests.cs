using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The one piece of arithmetic behind the rescan hint. USNs are byte offsets into the
///     journal, so the at-least size is exact integer arithmetic over them: no clock, rate or
///     estimate.
/// </summary>
[TestClass]
public class JournalSizeArithmeticTests
{
    [TestMethod]
    public void SizeThatWouldHaveRetained_AddsOneAllocationDeltaAfterRoundingTheSpan()
    {
        // 100 bytes behind the tip rounds to two units, then the trimming margin adds one.
        Assert.AreEqual(192L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 900, nextUsn: 1_000, allocationDelta: 64));
        // An exact two-unit span still receives the one-unit trimming margin.
        Assert.AreEqual(192L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 872, nextUsn: 1_000, allocationDelta: 64));
        // One byte over two units rounds to three, then receives the one-unit margin.
        Assert.AreEqual(256L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 871, nextUsn: 1_000, allocationDelta: 64));
        // A delta of one adds exactly one byte after rounding.
        Assert.AreEqual(101L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 900, nextUsn: 1_000, allocationDelta: 1));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_RealisticJournalNumbers()
    {
        // A checkpoint 300 MB behind the tip of a journal whose allocation delta is 64 MB:
        // five 64 MB units cover the span, then one unit supplies the trimming margin.
        const long megabyte = 1024 * 1024;
        Assert.AreEqual(384 * megabyte,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(
                checkpointUsn: 1_000_000_000, nextUsn: 1_000_000_000 + 300 * megabyte,
                allocationDelta: 64 * megabyte));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_ZeroSpan_IsOneAllocationDelta()
    {
        Assert.AreEqual(64L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 1_000, nextUsn: 1_000, allocationDelta: 64));
    }

    [TestMethod]
    public void SizeThatWouldHaveRetained_CheckpointPastTheTip_IsClampedToOneAllocationDelta()
    {
        // Unreachable from a real journal read, since the caller only asks when the
        // checkpoint is behind the tip, but the arithmetic still treats a negative span the
        // same as a zero one rather than producing a negative or nonsensical result.
        Assert.AreEqual(64L,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn: 1_001, nextUsn: 1_000, allocationDelta: 64));
        Assert.AreEqual(64L,
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
    public void SizeThatWouldHaveRetained_ExtraAllocationDeltaMovesTheOverflowBoundary()
    {
        const long largestAlignedResult = long.MaxValue - (long.MaxValue % 64);
        const long largestSpanThatFits = largestAlignedResult - 64;

        Assert.AreEqual(largestAlignedResult,
            JournalSizeArithmetic.SizeThatWouldHaveRetained(
                checkpointUsn: 0, nextUsn: largestSpanThatFits, allocationDelta: 64));

        // One more byte rounds the span to the largest aligned value, leaving no room for the margin.
        Assert.ThrowsException<OverflowException>(() =>
            JournalSizeArithmetic.SizeThatWouldHaveRetained(
                checkpointUsn: 0, nextUsn: largestSpanThatFits + 1, allocationDelta: 64));
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
