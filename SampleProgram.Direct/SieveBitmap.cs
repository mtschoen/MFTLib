using System.Numerics;

namespace SampleProgram.Direct;

/// <summary>
///     One duplicate-name sieve pass: two bits per bucket record first and repeated sightings.
///     Bucket collisions admit extra candidates but never discard a truly repeated hash.
/// </summary>
internal sealed class SieveBitmap
{
    internal const int MinimumBucketCount = 1 << 16;
    internal const int MaximumBucketCount = 1 << 27;
    const int DefaultBucketCount = 1 << 24;
    const uint SeedSpreadMultiplier = 0x9E3779B9;

    readonly int _seed;
    readonly int _shift;
    readonly ulong[] _seenAgain;
    ulong[] _seenOnce;

    internal SieveBitmap(int bucketCount, int seed)
    {
        if (bucketCount < 64 || !BitOperations.IsPow2(bucketCount))
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount), bucketCount,
                "Bucket count must be a power of two of at least 64.");
        }

        _seed = seed;
        _shift = 32 - BitOperations.Log2((uint)bucketCount);
        _seenOnce = new ulong[bucketCount / 64];
        _seenAgain = new ulong[bucketCount / 64];
    }

    /// <summary>Bytes retained by this pass's bit arrays.</summary>
    internal long ByteCount => (_seenOnce.LongLength + _seenAgain.LongLength) * sizeof(ulong);

    internal void Record(int hash)
    {
        var bucket = BucketIndexFor(hash);
        var word = bucket >> 6;
        var bit = 1UL << (bucket & 63);
        if ((_seenOnce[word] & bit) != 0)
        {
            _seenAgain[word] |= bit;
        }
        else
        {
            _seenOnce[word] |= bit;
        }
    }

    internal bool MayRepeat(int hash)
    {
        var bucket = BucketIndexFor(hash);
        return (_seenAgain[bucket >> 6] & (1UL << (bucket & 63))) != 0;
    }

    /// <summary>Releases first-sighting bits. Only MayRepeat may be used after completing a pass.</summary>
    internal void CompletePass()
    {
        _seenOnce = [];
    }

    /// <summary>Four buckets per expected row, rounded up and clamped; unknown counts use the default.</summary>
    internal static int ComputeBucketCount(long expectedRowCount)
    {
        if (expectedRowCount <= 0)
        {
            return DefaultBucketCount;
        }

        var clampedRowCount = Math.Min(expectedRowCount, MaximumBucketCount);
        var target = clampedRowCount * 4;
        if (target > MaximumBucketCount)
        {
            return MaximumBucketCount;
        }

        var roundedUp = (int)BitOperations.RoundUpToPowerOf2((ulong)target);
        return Math.Max(roundedUp, MinimumBucketCount);
    }

    internal int BucketIndexFor(int hash)
    {
        var seasoned = (uint)hash + unchecked((uint)_seed * SeedSpreadMultiplier);
        return (int)(Fmix32(seasoned) >> _shift);
    }

    static uint Fmix32(uint value)
    {
        value ^= value >> 16;
        value *= 0x85EBCA6Bu;
        value ^= value >> 13;
        value *= 0xC2B2AE35u;
        value ^= value >> 16;
        return value;
    }
}
