namespace MFTLib.Index;

/// <summary>
///     One Windows drive root with the facts <see cref="IndexedDrive.EnumerateWindowsVolumes" /> read and its
///     verdict. A consumer branches on the fields and renders its own words.
/// </summary>
/// <param name="Drive">The drive letter of <paramref name="RootDirectory" />.</param>
/// <param name="RootDirectory">The drive root, for example <c>C:\</c>.</param>
/// <param name="DriveType">The operating system's classification of the drive.</param>
/// <param name="IsReady">
///     True only when the drive was read as ready. Readiness is read for fixed drives alone, so every other
///     drive type reports false.
/// </param>
/// <param name="Format">
///     The file system name, for example <c>NTFS</c> or <c>exFAT</c>. Null unless the drive is a ready fixed drive
///     whose format was read.
/// </param>
/// <param name="IsIndexable">True only for a ready fixed NTFS drive, which an MFT scan can index.</param>
/// <param name="Reason">A display reason when <paramref name="IsIndexable" /> is false; null when it is true.</param>
public sealed record WindowsVolume(
    DriveKey Drive,
    string RootDirectory,
    DriveType DriveType,
    bool IsReady,
    string? Format,
    bool IsIndexable,
    string? Reason);
