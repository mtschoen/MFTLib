namespace MFTLib.Index;

/// <summary>
///     Why a root failed, as <see cref="RootFailure.Kind" />. The distinction a consumer
///     needs to decide whether a scan would recover the root: cache failures can be recovered
///     by an available producer (<see cref="FileIndex.RescanAsync" />), <see cref="ProducerFailed" />
///     means a selected producer (MFT or enumeration) ran and failed, and <see cref="MftUnavailable" />
///     means the MFT could not serve the root at all.
/// </summary>
public enum RootFailureKind
{
    /// <summary>
    ///     <see cref="CacheMode.ExistingOnly" /> found no usable cache block
    ///     (missing, corrupt, or incompatible) and forbade the scan that would have built one.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a root and clears its failure.
    /// </summary>
    CacheDeclined,

    /// <summary>
    ///     The volume's MFT producer failed during opening or its latest rescan;
    ///     <see cref="RootFailure.Message" /> carries the producer's error.
    /// </summary>
    ProducerFailed,

    /// <summary>
    ///     A cache-only open could not acquire the cache block's owner lock because of contention
    ///     or an access or I/O failure, so this index never validated, renamed, or deleted the
    ///     file. A non-cache-only open scans into a private block instead of failing.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a root and clears its failure.
    /// </summary>
    InUse,

    /// <summary>
    ///     A cache-only open found a cached block built for another identity: its consumer cache tag
    ///     (FourCC or version) differs from <see cref="FileIndexOptions.CacheTag" />, or its scan shape
    ///     differs from the one the open's row-shaping options make
    ///     (<see cref="BlockValidationResult.WrongCacheTag" /> or <see cref="BlockValidationResult.WrongScanShape" />).
    ///     The block stays in place, and the scan that would build a replacement was forbidden.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a root and clears its failure.
    /// </summary>
    CacheIdentityMismatch,

    /// <summary>
    ///     The MFT could not serve this root: <see cref="ProducerSelection.MftOnly" /> found it ineligible, or a broker
    ///     launch or elevation failed during a scan. <see cref="RootStatus.MftUnavailable" /> says why. The producer is
    ///     fixed for the index's life, so a rescan never walks instead; it fails again until the cause is gone
    ///     (a source supplied on a reopen, an elevation prompt accepted).
    /// </summary>
    MftUnavailable
}
