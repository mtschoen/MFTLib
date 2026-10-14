namespace MFTLib.Index;

/// <summary>
///     One drive to index. <paramref name="RootDirectory" /> is where an enumeration walk
///     starts, and on a platform without drive letters the caller assigns
///     <paramref name="Drive" /> as a display and lookup key. The volume serial is part of
///     the block's file name so a re-lettered drive never matches the wrong block.
///     <paramref name="ProducerPolicy" /> selects how this drive's block is built on scans and rescans.
/// </summary>
/// <exception cref="ArgumentException">
///     <paramref name="Drive" /> is the default key, or <paramref name="RootDirectory" /> is empty or whitespace.
/// </exception>
/// <exception cref="ArgumentNullException"><paramref name="RootDirectory" /> is null.</exception>
/// <exception cref="ArgumentOutOfRangeException">
///     <paramref name="ProducerPolicy" /> is not a defined <see cref="MFTLib.Index.ProducerPolicy" />.
/// </exception>
public sealed partial record IndexedDrive(DriveKey Drive, string RootDirectory, uint VolumeSerial,
    ProducerPolicy ProducerPolicy = ProducerPolicy.Mft)
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
    ///     Which producer builds this drive's block; assigning an undefined policy throws
    ///     <see cref="ArgumentOutOfRangeException" />.
    /// </summary>
    public ProducerPolicy ProducerPolicy { get; init => field = ValidateProducerPolicy(value); } =
        ValidateProducerPolicy(ProducerPolicy);

    internal IndexedDrive Validated()
    {
        _ = Drive.Validated();
        _ = ValidateRootDirectory(RootDirectory);
        _ = ValidateProducerPolicy(ProducerPolicy);
        return this;
    }

    static string ValidateRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return rootDirectory;
    }

    static ProducerPolicy ValidateProducerPolicy(ProducerPolicy producerPolicy) =>
        Enum.IsDefined(producerPolicy)
            ? producerPolicy
            : throw new ArgumentOutOfRangeException(nameof(producerPolicy), producerPolicy,
                "Unknown producer policy.");
}
