namespace MFTLib.Index;

/// <summary>
///     Why a <see cref="DriveState.Failed" /> drive has no block. The distinction a consumer
///     needs to decide whether a scan would recover the drive: a cache-only decline is cured by
///     the scan <see cref="FileIndex.RescanAsync" /> runs, while a producer failure is the scan
///     itself failing. Meaningful only while <see cref="DriveStatus.State" /> is
///     <see cref="DriveState.Failed" />; every other state reads <see cref="None" />.
/// </summary>
public enum DriveFailureKind
{
    /// <summary>
    ///     The drive is not failed, including one that is <see cref="DriveState.Offline" />.
    /// </summary>
    None,

    /// <summary>
    ///     <see cref="FileIndexOptions.InitialOpenCacheOnly" /> found no usable cache block
    ///     (missing, corrupt, or incompatible) and forbade the scan that would have built one.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears this kind.
    /// </summary>
    CacheDeclined,

    /// <summary>
    ///     The drive's MFT producer failed during opening or its latest rescan;
    ///     <see cref="DriveStatus.MftProducerFailureMessage" /> carries the producer's error.
    /// </summary>
    ProducerFailed,

    /// <summary>
    ///     A cache-only open found the cache block's owner lock held by another live
    ///     <see cref="FileIndex" />, so this index never validated, renamed, or deleted the
    ///     file. A non-cache-only open scans into a private block instead of failing.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears this kind.
    /// </summary>
    InUse,

    /// <summary>
    ///     A cache-only open found a cached block whose consumer cache tag (FourCC or version)
    ///     differs from <see cref="FileIndexOptions.CacheTag" />. The block was discarded and
    ///     best-effort deleted, and the scan that would build a replacement was forbidden.
    ///     <see cref="FileIndex.RescanAsync" /> scans such a drive and clears this kind.
    /// </summary>
    CacheTagMismatch
}
