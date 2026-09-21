using System.Numerics;

namespace MFTLib.Index;

/// <summary>
///     The arithmetic behind <see cref="JournalCheckpointLoss" />. A USN is a byte offset into
///     the change journal, so the distance between two of them is a byte count and the journal
///     size that would have kept a checkpoint is that distance rounded up to the unit NTFS
///     allocates in. Exact integer arithmetic over numbers the volume reports: there is no
///     clock, no rate and no estimate anywhere in it.
/// </summary>
static class JournalSizeArithmetic
{
    /// <summary>
    ///     The journal maximum size that would have kept <paramref name="checkpointUsn" />
    ///     readable: the bytes between it and the journal's tip, rounded up to a whole
    ///     <paramref name="allocationDelta" />, because NTFS grows and trims the journal in
    ///     units of that size.
    /// </summary>
    /// <param name="checkpointUsn">The USN a cached block was resumable from.</param>
    /// <param name="nextUsn">The USN the journal's next record will be written at.</param>
    /// <param name="allocationDelta">The journal's allocation unit; must be positive.</param>
    /// <returns>
    ///     The rounded-up size, or zero when the checkpoint was at or past the tip and so
    ///     implies no size at all.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A USN is negative, or <paramref name="allocationDelta" /> is not positive.
    /// </exception>
    /// <exception cref="OverflowException">
    ///     Rounding the span up to the next allocation unit does not fit in a
    ///     <see cref="long" />.
    /// </exception>
    public static long SizeThatWouldHaveRetained(long checkpointUsn, long nextUsn, long allocationDelta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointUsn);
        ArgumentOutOfRangeException.ThrowIfNegative(nextUsn);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allocationDelta);

        if (checkpointUsn >= nextUsn)
        {
            return 0;
        }

        // BigInteger for the round-up alone: the span always fits a long, but adding the
        // rounding slack to a span near long.MaxValue does not, and that must surface as an
        // overflow rather than wrap into a small, believable journal size.
        var span = (BigInteger)nextUsn - checkpointUsn;
        var units = (span + allocationDelta - 1) / allocationDelta;
        return checked((long)(units * allocationDelta));
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
