namespace MFTLib.Index;

/// <summary>
///     One Windows volume root with the facts <see cref="EnumerateAll" /> read and its
///     verdict. A consumer branches on the fields and renders its own words.
/// </summary>
public sealed partial record WindowsVolume
{
    /// <summary>Creates the identity of one volume root; the readiness facts and verdict are set by initializers.</summary>
    /// <param name="volume">The drive letter of <paramref name="rootDirectory" />.</param>
    /// <param name="rootDirectory">The volume root, for example <c>C:\</c>.</param>
    /// <param name="driveType">The operating system's classification of the root.</param>
    internal WindowsVolume(VolumeKey volume, string rootDirectory, DriveType driveType)
    {
        Volume = volume;
        RootDirectory = rootDirectory;
        DriveType = driveType;
    }

    /// <summary>The drive letter of <see cref="RootDirectory" />.</summary>
    public VolumeKey Volume { get; init => field = value.Validated(); }

    /// <summary>The volume root, for example <c>C:\</c>.</summary>
    public string RootDirectory { get; init; }

    /// <summary>The operating system's classification of the root.</summary>
    public DriveType DriveType { get; init; }

    /// <summary>
    ///     True only when the root was read as ready. Readiness is read for fixed and removable roots alone, so
    ///     every other drive type reports false.
    /// </summary>
    public bool IsReady { get; init; }

    /// <summary>
    ///     The file system name, for example <c>NTFS</c> or <c>exFAT</c>. Null unless the root is a ready fixed or
    ///     removable root whose format was read.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>True only for a ready fixed or removable NTFS volume, which <c>ProducerSelection.Automatic</c> scans through the MFT.</summary>
    public bool IsIndexable { get; init; }

    /// <summary>
    ///     Display text explaining why <see cref="IsIndexable" /> is false; null when it is true. It is not a stable
    ///     format: consumers show it and never parse it, and branch on <see cref="DriveType" />, <see cref="IsReady" />
    ///     and <see cref="Format" /> instead.
    /// </summary>
    public string? Reason { get; init; }
}
