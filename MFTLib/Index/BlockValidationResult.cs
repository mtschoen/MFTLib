namespace MFTLib.Index;

/// <summary>
///     Why a block on disk was accepted or rejected. Every rejection means the same thing to
///     the caller (discard the file and rescan) but the reason is logged so a recurring
///     rejection is diagnosable rather than an invisible repeated cold scan.
/// </summary>
public enum BlockValidationResult
{
    Valid,
    WrongMagic,
    WrongFormatVersion,
    Incomplete,
    WrongVolumeSerial,
    InconsistentRegions,
    WrongRootDirectory,
    InvalidNameDescriptor,

    /// <summary>
    ///     The block's stored cache tag cannot be trusted for a warm start: either
    ///     <see cref="BlockHeader.Validate" /> found the stored FourCC bytes are not valid
    ///     ASCII (on-disk bytes are untrusted, so this is checked structurally, independent of
    ///     any requested identity; see <see cref="CacheTag" />'s constructor invariant), or the
    ///     caller found <see cref="BlockHeader.CacheTag" /> does not match the consumer's
    ///     requested identity. Either way the cached bytes may reflect a different scan shape.
    /// </summary>
    WrongCacheTag
}
