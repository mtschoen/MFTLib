using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Creates one drive's named block section, its block view, and the section's lifetime. The
///     broker writes the scan into the section by name. Disposing <c>Lifetime</c> only unpublishes
///     the name: the scan does so once the section is written, while the caller keeps using
///     <c>Block</c>, so a lifetime whose disposal also invalidated the block's view would break
///     every scan.
/// </summary>
/// <param name="driveLetter">The bare upper-case drive letter being scanned.</param>
/// <param name="options">The block's layout, sized from the drive's volume information.</param>
public delegate (string SectionName, BlockFile Block, IDisposable Lifetime) BrokerBlockSectionFactory(
    char driveLetter, BlockFileCreateOptions options);
