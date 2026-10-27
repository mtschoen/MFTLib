namespace MFTLib.Index;

/// <summary>
///     Why a block on disk was rejected. Every rejection means the same thing to
///     the caller (discard the file and rescan) but the reason is logged so a recurring
///     rejection is diagnosable rather than an invisible repeated cold scan.
/// </summary>
public enum BlockValidationResult
{
    /// <summary>The file is missing, unreadable, too short, or does not begin with the block-format magic value.</summary>
    WrongMagic,
    /// <summary>The file uses an unsupported block-format version.</summary>
    WrongFormatVersion,
    /// <summary>The completion marker was never published, so the partially written file must be discarded.</summary>
    Incomplete,
    /// <summary>The cached identifiers arise from another volume, so they cannot identify files on the requested volume.</summary>
    WrongVolumeSerial,
    /// <summary>
    ///     The on-disk layout cannot be mapped safely because its regions contradict the header, or the root key
    ///     text stored after the header is malformed.
    /// </summary>
    InconsistentRegions,
    /// <summary>
    ///     The cache covers another root, so it cannot satisfy this open request: another tree on the same volume, or a
    ///     file whose name is not the one its stored root key and volume serial derive.
    /// </summary>
    WrongRootDirectory,
    /// <summary>A row could address unvalidated name-pool bytes, so the cache cannot be read safely.</summary>
    InvalidNameDescriptor,

    /// <summary>
    ///     The block's stored cache tag cannot be trusted for a warm start: either header
    ///     validation found the stored FourCC bytes are not valid
    ///     ASCII (on-disk bytes are untrusted, so this is checked structurally, independent of
    ///     any requested identity; see <see cref="CacheTag" />'s constructor invariant), or the
    ///     stored cache tag does not match the consumer's
    ///     requested identity. Either way the consumer's meaning of the cached bytes may differ.
    /// </summary>
    WrongCacheTag,

    /// <summary>
    ///     The block was built under other row-shaping options than the open requests: another
    ///     <see cref="FileIndexOptions.RetainedFileNames" />, <see cref="FileIndexOptions.DirectoryWalk" /> for an
    ///     enumeration root, or <see cref="MftScanOptions.IncludeFreed" /> for an MFT volume. The library
    ///     compares this itself, so the consumer's <see cref="CacheTag" /> need not change with these options.
    /// </summary>
    WrongScanShape
}
