namespace MFTLib.Index;

/// <summary>
///     Shared bit-array and bucket-hashing primitives for the duplicate-name sieve chain.
///     <see cref="NameHashTable" /> (a sieve pass under construction) and
///     <see cref="MayRepeatBitmap" /> (a completed pass retained for a later refinement pass to
///     consult, see <see cref="DuplicateNameFinder" />) both address the same kind of fixed-size
///     bit array through a name hash and a per-pass seed, so the mapping from (hash, seed) to
///     bucket, and from bucket to bit, live here once rather than twice.
/// </summary>
internal static class SieveBits
{
    // 2^32 divided by the golden ratio, rounded to the nearest odd integer: spreads a seed
    // across the full 32-bit range before it is folded into a hash, so adjacent seeds (0, 1, 2,
    // ...) still produce effectively unrelated bucket assignments once mixed.
    const uint SeedSpreadMultiplier = 0x9E3779B9;

    /// <summary>
    ///     Which bucket a name hash falls into under one seed. Different seeds must scatter the
    ///     same name hash into effectively independent buckets, or a refinement chain built from
    ///     repeated passes would keep colliding on the same name pairs and would never shrink its
    ///     candidate set. Folding the seed in before a Murmur3-style 32-bit finalizer
    ///     (<see cref="Fmix32" />) provides that: every output bit depends on every input bit, so
    ///     two different seeds produce effectively unrelated bucket assignments for the same hash
    ///     rather than merely an offset one.
    /// </summary>
    internal static int BucketIndexFor(int hash, int seed, int shift)
    {
        var seasoned = (uint)hash + unchecked((uint)seed * SeedSpreadMultiplier);
        return (int)(Fmix32(seasoned) >> shift);
    }

    internal static bool GetBit(ulong[] words, int index)
    {
        return (words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    internal static void SetBit(ulong[] words, int index)
    {
        words[index >> 6] |= 1UL << (index & 63);
    }

    // Murmur3's 32-bit finalizer: three xor-shift/multiply rounds that avalanche every input bit
    // into every output bit, which is what keeps different seeds independent of each other
    // rather than merely offset.
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
