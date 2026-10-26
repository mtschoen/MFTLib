namespace MFTLib.Index;

/// <summary>
///     Why a drive failed, as <see cref="DriveFailure.Kind" />. The distinction a consumer
///     needs to decide whether a scan would recover the drive: cache failures can be recovered
///     by an available producer (<see cref="FileIndex.RescanAsync" />), <see cref="ProducerFailed" />
///     means a selected producer (MFT or enumeration) ran and failed, and <see cref="MftSourceMissing" />
///     means an MFT drive needed a scan but no MFT source is configured.
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
    ///     The block stays in place, and the scan that would build a replacement was forbidden.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears its failure.
    /// </summary>
    CacheIdentityMismatch,

    /// <summary>
    ///     An MFT drive needs a scan but <see cref="FileIndexOptions.MftSource" /> is null.
    ///     <see cref="FileIndex.RescanAsync" /> cannot recover the drive while the source remains null.
    ///     Reopen the index with an MFT source to enable scanning.
    /// </summary>
    MftSourceMissing
}
