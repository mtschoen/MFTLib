using System.Numerics;

namespace MFTLib.Index;

/// <summary>
///     One pass of the transient fixed-size hash sieve that <see cref="DuplicateNameFinder" />
///     chains together. Every pass is seeded so repeated construction with a different seed
///     produces effectively independent bucket assignments for the same name hash (see
///     <see cref="SieveBits" />): within a single pass this distinguishes "seen zero or one
///     times" from "seen two or more times" without counting exactly and without growing, so
///     memory is bounded by the caller's row estimate rather than by the number of distinct
///     names actually observed. Two fixed-size bit arrays back a pass, <c>seenOnce</c> and
///     <c>seenAgain</c>: <see cref="Increment" /> sets a hash's bucket bit in <c>seenOnce</c> the
///     first time it is observed and sets the same bucket's bit in <c>seenAgain</c> on every
///     later observation; <see cref="MayRepeat" /> answers whether a hash's bucket has its
///     <c>seenAgain</c> bit set. Once a pass is finished, <see cref="ToMayRepeatBitmap" /> hands
///     off only the <c>seenAgain</c> array so <c>seenOnce</c> becomes eligible for collection;
///     see <see cref="MayRepeatBitmap" />.
/// </summary>
/// <remarks>
///     Two distinct name hashes that land in the same bucket are indistinguishable within one
///     pass, so a bucket collision can only make <see cref="MayRepeat" /> report a false
///     positive, never a false negative: <see cref="DuplicateNameFinder" /> runs further
///     refinement passes with different seeds specifically to shrink that false-positive set
///     before the final materialization pass, but even a single pass alone never drops a real
///     duplicate group. Bucket count is a power of two derived from the caller's expected row
///     count (<see cref="ComputeBucketCount" />), clamped between <see cref="MinimumBucketCount" />
///     and <see cref="MaximumBucketCount" />: at the maximum, each bit array is 16 MiB. See
///     <see cref="DuplicateNameFinder" /> for the whole refinement chain's peak memory.
/// </remarks>
internal sealed class NameHashTable
{
    internal const int MinimumBucketCount = 1 << 16;
    internal const int MaximumBucketCount = 1 << 27;
    const int DefaultBucketCount = 1 << 24;

    readonly int _bucketCount;
    readonly int _shift;
    readonly int _seed;
    readonly ulong[] _seenOnce;
    readonly ulong[] _seenAgain;

    internal NameHashTable(long expectedRowCount = 0, int seed = 0)
        : this(ComputeBucketCount(expectedRowCount), seed)
    {
    }

    NameHashTable(int bucketCount, int seed)
    {
        _bucketCount = bucketCount;
        _shift = 32 - BitOperations.Log2((uint)_bucketCount);
        _seed = seed;
        var wordCount = _bucketCount / 64;
        _seenOnce = new ulong[wordCount];
        _seenAgain = new ulong[wordCount];
    }

    /// <summary>Number of addressable buckets, always a power of two.</summary>
    internal int BucketCount => _bucketCount;

    /// <summary>Total bytes held by this pass's two bit arrays combined.</summary>
    internal long ByteCount => 2L * _seenOnce.Length * sizeof(ulong);

    /// <summary>
    ///     Builds a pass with an exact bucket count rather than one derived from a row estimate.
    ///     Used by <see cref="DuplicateNameFinder" /> so every pass in a chain shares one bucket
    ///     count, and by tests that force a small count to get a high collision rate out of a
    ///     small synthetic block.
    /// </summary>
    internal static NameHashTable ForBucketCount(int bucketCount, int seed = 0)
    {
        if (bucketCount <= 0 || (bucketCount & (bucketCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount), bucketCount,
                "Bucket count must be a positive power of two.");
        }

        return new NameHashTable(bucketCount, seed);
    }

    /// <summary>Records one more row carrying <paramref name="hash" />.</summary>
    internal void Increment(int hash)
    {
        var index = BucketIndexFor(hash);
        if (SieveBits.GetBit(_seenOnce, index))
        {
            SieveBits.SetBit(_seenAgain, index);
        }
        else
        {
            SieveBits.SetBit(_seenOnce, index);
        }
    }

    /// <summary>
    ///     Whether <paramref name="hash" /> may have been carried by two or more rows since this
    ///     pass was constructed. Never a false negative for a hash that truly repeated; can be a
    ///     false positive when a distinct hash lands in the same bucket as one that did repeat.
    /// </summary>
    internal bool MayRepeat(int hash)
    {
        return SieveBits.GetBit(_seenAgain, BucketIndexFor(hash));
    }

    /// <summary>
    ///     The bucket a hash maps to under this pass's seed. Exposed so a test can locate two
    ///     distinct hash values that land in the same bucket and confirm the sieve's documented
    ///     over-count contract rather than an under-count.
    /// </summary>
    internal int BucketIndexFor(int hash)
    {
        return SieveBits.BucketIndexFor(hash, _seed, _shift);
    }

    /// <summary>
    ///     Hands off this pass's completed "may repeat" verdicts as a standalone bitmap and lets
    ///     this table's reference to <c>seenOnce</c> go out of scope, so a refinement chain that
    ///     keeps several completed passes around only pays for one bit array per pass rather than
    ///     two.
    /// </summary>
    internal MayRepeatBitmap ToMayRepeatBitmap()
    {
        return new MayRepeatBitmap(_shift, _seed, _seenAgain);
    }

    /// <summary>
    ///     Pure sizing function, exposed so tests can exercise the clamp boundaries without
    ///     constructing (and therefore allocating) a table at each size. Targets four buckets per
    ///     expected row before rounding up to a power of two and clamping, which keeps the
    ///     sieve's false-positive rate low for the row counts real drives produce while never
    ///     exceeding <see cref="MaximumBucketCount" />.
    /// </summary>
    internal static int ComputeBucketCount(long expectedRowCount)
    {
        if (expectedRowCount <= 0)
        {
            return DefaultBucketCount;
        }

        // Clamp before multiplying by four so an enormous caller-supplied estimate cannot
        // overflow; any row count at or above the maximum bucket count already saturates the
        // clamp below regardless of the exact product.
        var clampedRowCount = Math.Min(expectedRowCount, MaximumBucketCount);
        var target = clampedRowCount * 4;
        if (target > MaximumBucketCount)
        {
            return MaximumBucketCount;
        }

        var roundedUp = (int)BitOperations.RoundUpToPowerOf2((ulong)target);
        return Math.Max(roundedUp, MinimumBucketCount);
    }
}
