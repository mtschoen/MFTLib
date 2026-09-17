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
    ProducerFailed
}
