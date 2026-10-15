namespace MFTLib.Index;

/// <summary>
///     One drive to index. <paramref name="RootDirectory" /> is where an enumeration walk
///     starts, and on a platform without drive letters the caller assigns
///     <paramref name="Drive" /> as a display and lookup key. The volume serial is part of
///     the block's file name so a re-lettered drive never matches the wrong block.
///     <paramref name="Producer" /> selects how this drive's block is built on scans and rescans.
/// </summary>
/// <exception cref="ArgumentException">
///     <paramref name="Drive" /> is the default key, or <paramref name="RootDirectory" /> is empty or whitespace.
/// </exception>
/// <exception cref="ArgumentNullException"><paramref name="RootDirectory" /> is null.</exception>
/// <exception cref="ArgumentOutOfRangeException">
///     <paramref name="Producer" /> is not a defined <see cref="ProducerKind" />.
/// </exception>
public sealed partial record IndexedDrive(DriveKey Drive, string RootDirectory, uint VolumeSerial,
    ProducerKind Producer = ProducerKind.Mft)
{
    /// <summary>Selects the index's drive identity; assigning the default key throws <see cref="ArgumentException" />.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); } = Drive.Validated();

    /// <summary>
    ///     Root directory where an enumeration walk starts; assigning null throws
    ///     <see cref="ArgumentNullException" /> and empty or whitespace throws <see cref="ArgumentException" />.
    /// </summary>
    public string RootDirectory { get; init => field = ValidateRootDirectory(value); } =
        ValidateRootDirectory(RootDirectory);

    /// <summary>
    ///     Which producer builds this drive's block; assigning the default or an undefined kind throws
    ///     <see cref="ArgumentOutOfRangeException" />. <see cref="ProducerKind.Mft" /> uses
    ///     <see cref="FileIndexOptions.MftSource" />. A drive whose producer fails without a usable block
    ///     is <see cref="DriveState.Failed" />; a failed replacement retains the existing block and reports
    ///     <see cref="DriveState.Ready" /> with a producer failure. It is never rebuilt by a directory walk.
    ///     <see cref="ProducerKind.Enumeration" /> walks the directory tree, ignoring
    ///     <see cref="FileIndexOptions.MftSource" /> entirely, and is never selected on a caller's behalf.
    /// </summary>
    public ProducerKind Producer { get; init => field = ValidateProducer(value); } =
        ValidateProducer(Producer);

    internal IndexedDrive Validated()
    {
        _ = Drive.Validated();
        _ = ValidateRootDirectory(RootDirectory);
        _ = ValidateProducer(Producer);
        return this;
    }

    static string ValidateRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return rootDirectory;
    }

    static ProducerKind ValidateProducer(ProducerKind producer) =>
        Enum.IsDefined(producer)
            ? producer
            : throw new ArgumentOutOfRangeException(nameof(producer), producer,
                "Unknown producer kind.");
}
