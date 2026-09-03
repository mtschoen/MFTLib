namespace MFTLib.Index;

/// <summary>
///     Test-only tuning seam for <see cref="DuplicateNameFinder" />. Production callers always
///     use <see cref="Default" />, which sizes every sieve pass from the snapshot's own row
///     count. A test can instead force a specific bucket count so a small synthetic block
///     produces the same high collision rate a real multi-million-row drive would, without
///     having to build a multi-million-row block to prove the refinement chain behaves.
/// </summary>
internal readonly struct DuplicateNameSieveOptions
{
    internal static DuplicateNameSieveOptions Default => new(bucketCountOverride: null);

    internal DuplicateNameSieveOptions(int? bucketCountOverride)
    {
        BucketCountOverride = bucketCountOverride;
    }

    /// <summary>
    ///     When set, every pass in the chain uses this bucket count instead of one derived from
    ///     the snapshot's row count. Must be a positive power of two; <see cref="NameHashTable.ForBucketCount" />
    ///     enforces that.
    /// </summary>
    internal int? BucketCountOverride { get; }
}
