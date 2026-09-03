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
    InvalidNameDescriptor
}
