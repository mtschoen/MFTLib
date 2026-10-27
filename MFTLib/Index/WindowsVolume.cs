namespace MFTLib.Index;

/// <summary>
///     One Windows drive root with the facts <see cref="IndexedRoot.EnumerateWindowsVolumes" /> read and its
///     verdict. A consumer branches on the fields and renders its own words.
/// </summary>
public sealed record WindowsVolume
{
    /// <summary>Creates the identity of one drive root; the readiness facts and verdict are set by initializers.</summary>
    /// <param name="drive">The drive letter of <paramref name="rootDirectory" />.</param>
    /// <param name="rootDirectory">The drive root, for example <c>C:\</c>.</param>
    /// <param name="driveType">The operating system's classification of the drive.</param>
    internal WindowsVolume(VolumeKey drive, string rootDirectory, DriveType driveType)
    {
        Drive = drive;
        RootDirectory = rootDirectory;
        DriveType = driveType;
    }

    /// <summary>The drive letter of <see cref="RootDirectory" />.</summary>
    public VolumeKey Drive { get; init => field = value.Validated(); }

    /// <summary>The drive root, for example <c>C:\</c>.</summary>
    public string RootDirectory { get; init; }

    /// <summary>The operating system's classification of the drive.</summary>
    public DriveType DriveType { get; init; }

    /// <summary>
    ///     True only when the drive was read as ready. Readiness is read for fixed drives alone, so every other
    ///     drive type reports false.
    /// </summary>
    public bool IsReady { get; init; }

    /// <summary>
    ///     The file system name, for example <c>NTFS</c> or <c>exFAT</c>. Null unless the drive is a ready fixed drive
    ///     whose format was read.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>True only for a ready fixed NTFS drive, which an MFT scan can index.</summary>
    public bool IsIndexable { get; init; }

    /// <summary>
    ///     Display text explaining why <see cref="IsIndexable" /> is false; null when it is true. It is not a stable
    ///     format: consumers show it and never parse it, and branch on <see cref="DriveType" />, <see cref="IsReady" />
    ///     and <see cref="Format" /> instead.
    /// </summary>
    public string? Reason { get; init; }
}
