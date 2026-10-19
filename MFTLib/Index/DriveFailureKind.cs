namespace MFTLib.Index;

/// <summary>
///     Why a drive failed, as <see cref="DriveFailure.Kind" />. The distinction a consumer
///     needs to decide whether a scan would recover the drive: a cache-only decline is cured by
///     the scan <see cref="FileIndex.RescanAsync" /> runs, while a producer failure is the scan
///     itself failing.
/// </summary>
public enum DriveFailureKind
{
    /// <summary>
    ///     <see cref="CacheMode.ExistingOnly" /> found no usable cache block
    ///     (missing, corrupt, or incompatible) and forbade the scan that would have built one.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears its failure.
    /// </summary>
    CacheDeclined,

    /// <summary>
    ///     The drive's MFT producer failed during opening or its latest rescan;
    ///     <see cref="DriveFailure.Message" /> carries the producer's error.
    /// </summary>
    ProducerFailed,

    /// <summary>
    ///     A cache-only open could not acquire the cache block's owner lock because of contention
    ///     or an access or I/O failure, so this index never validated, renamed, or deleted the
    ///     file. A non-cache-only open scans into a private block instead of failing.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears its failure.
    /// </summary>
    InUse,

    /// <summary>
    ///     A cache-only open found a cached block built for another identity: its consumer cache tag
    ///     (FourCC or version) differs from <see cref="FileIndexOptions.CacheTag" />, or its scan shape
    ///     differs from the one the open's row-shaping options make
    ///     (<see cref="BlockValidationResult.WrongCacheTag" /> or <see cref="BlockValidationResult.WrongScanShape" />).
    ///     The block was discarded and best-effort deleted, and the scan that would build a replacement was forbidden.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears its failure.
    /// </summary>
    CacheIdentityMismatch
}
