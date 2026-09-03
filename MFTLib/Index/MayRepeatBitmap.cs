namespace MFTLib.Index;

/// <summary>
///     One completed sieve pass's "may repeat" verdicts, retained only as long as a later
///     refinement pass in <see cref="DuplicateNameFinder" /> needs to consult it. Where
///     <see cref="NameHashTable" /> holds two bit arrays while a pass is actively being built
///     (<c>seenOnce</c> and <c>seenAgain</c>), a completed pass only ever needs to answer
///     <see cref="Contains" /> afterward, so <see cref="NameHashTable.ToMayRepeatBitmap" /> hands
///     off just the <c>seenAgain</c> array and lets the rest of that pass's state (in particular
///     <c>seenOnce</c>) become eligible for collection. A chain of completed passes therefore
///     costs one bit array each, not two.
/// </summary>
internal sealed class MayRepeatBitmap
{
    readonly int _shift;
    readonly int _seed;
    readonly ulong[] _bits;

    internal MayRepeatBitmap(int shift, int seed, ulong[] bits)
    {
        _shift = shift;
        _seed = seed;
        _bits = bits;
    }

    /// <summary>Bytes held by this one retained bit array.</summary>
    internal long ByteCount => _bits.Length * (long)sizeof(ulong);

    /// <summary>Whether this pass's sieve reported <paramref name="hash" /> as possibly repeated.</summary>
    internal bool Contains(int hash)
    {
        var index = SieveBits.BucketIndexFor(hash, _seed, _shift);
        return SieveBits.GetBit(_bits, index);
    }
}
