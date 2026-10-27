namespace MFTLib.Index;

/// <summary>
///     Why a root failed, as <see cref="RootFailure.Kind" />. The distinction a consumer
///     needs to decide whether a scan would recover the root: cache failures can be recovered
///     by an available producer (<see cref="FileIndex.RescanAsync" />), <see cref="ProducerFailed" />
///     means a selected producer (MFT or enumeration) ran and failed, and <see cref="MftSourceMissing" />
///     means an MFT volume needed a scan but no MFT source is configured.
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
    ///     An MFT volume needs a scan but <see cref="FileIndexOptions.MftSource" /> is null.
    ///     <see cref="FileIndex.RescanAsync" /> cannot recover the root while the source remains null.
    ///     Reopen the index with an MFT source to enable scanning.
    /// </summary>
    MftSourceMissing
}
