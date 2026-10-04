namespace MFTLib.Index;

/// <summary>
///     Why a block on disk was accepted or rejected. Every rejection means the same thing to
///     the caller (discard the file and rescan) but the reason is logged so a recurring
///     rejection is diagnosable rather than an invisible repeated cold scan.
/// </summary>
public enum BlockValidationResult
{
    /// <summary>Allows the caller to open the cached block without a replacement scan.</summary>
    Valid,
    /// <summary>The file does not begin with the block-format magic value.</summary>
    WrongMagic,
    /// <summary>The file uses an unsupported block-format version.</summary>
    WrongFormatVersion,
    /// <summary>The completion marker was never published, so the partially written file must be discarded.</summary>
    Incomplete,
    /// <summary>The cached identifiers arise from another volume, so they cannot identify files on the requested drive.</summary>
    WrongVolumeSerial,
    /// <summary>The on-disk layout cannot be mapped safely because its regions contradict the header.</summary>
    InconsistentRegions,
    /// <summary>The cache covers another tree on the same volume, so it cannot satisfy this open request.</summary>
    WrongRootDirectory,
    /// <summary>A row could address unvalidated name-pool bytes, so the cache cannot be read safely.</summary>
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
