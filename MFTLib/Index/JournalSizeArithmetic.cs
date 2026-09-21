using System.Numerics;

namespace MFTLib.Index;

/// <summary>
///     The arithmetic behind <see cref="JournalCheckpointLoss" />. A USN is a byte offset into
///     the change journal, so the distance between two of them is a byte count and the journal
///     minimum size that could have kept a checkpoint is that distance rounded up to the unit
///     NTFS allocates in, plus one more allocation unit. The extra unit follows the documented
///     trimming behavior in CREATE_USN_JOURNAL_DATA and USN_JOURNAL_DATA: the maximum is a
///     target, and NTFS can trim the journal to below it. This is exact integer arithmetic over
///     numbers the volume reports, not a live measurement: there is no clock, rate or cap.
/// </summary>
static class JournalSizeArithmetic
{
    /// <summary>
    ///     The journal maximum size that would need to be at least this large to have kept
    ///     <paramref name="checkpointUsn" /> readable: the bytes between it and the journal's
    ///     tip, rounded up to a whole <paramref name="allocationDelta" />, plus one more
    ///     allocation delta. The margin follows the documented trimming behavior in
    ///     CREATE_USN_JOURNAL_DATA and USN_JOURNAL_DATA, not a live measurement.
    /// </summary>
    /// <param name="checkpointUsn">The USN a cached block was resumable from.</param>
    /// <param name="nextUsn">The USN the journal's next record will be written at.</param>
    /// <param name="allocationDelta">The journal's allocation unit; must be positive.</param>
    /// <returns>
    ///     The rounded-up span plus one allocation delta.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A USN is negative, or <paramref name="allocationDelta" /> is not positive.
    /// </exception>
    /// <exception cref="OverflowException">
    ///     The rounded span plus one allocation delta does not fit in a <see cref="long" />.
    /// </exception>
    public static long SizeThatWouldHaveRetained(long checkpointUsn, long nextUsn, long allocationDelta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointUsn);
        ArgumentOutOfRangeException.ThrowIfNegative(nextUsn);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allocationDelta);

        // BigInteger keeps both the round-up and the extra allocation delta exact. A result
        // beyond long.MaxValue must surface as overflow rather than wrap into a small,
        // believable journal size.
        var span = BigInteger.Max(0, (BigInteger)nextUsn - checkpointUsn);
        var units = (span + allocationDelta - 1) / allocationDelta;
        return checked((long)((units + 1) * allocationDelta));
    }

    /// <summary>
    ///     How far behind the journal a checkpoint had fallen: the bytes between it and the
    ///     oldest record the journal still holds. Zero when the checkpoint is still inside the
    ///     journal, which is not a loss at all.
    /// </summary>
    /// <param name="checkpointUsn">The USN a cached block was resumable from.</param>
    /// <param name="firstUsn">The oldest USN the journal still retains.</param>
    /// <exception cref="ArgumentOutOfRangeException">A USN is negative.</exception>
    public static long BytesBehind(long checkpointUsn, long firstUsn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointUsn);
        ArgumentOutOfRangeException.ThrowIfNegative(firstUsn);
        return checkpointUsn >= firstUsn ? 0 : firstUsn - checkpointUsn;
    }
}
