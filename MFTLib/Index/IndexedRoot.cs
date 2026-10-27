namespace MFTLib.Index;

/// <summary>
///     One root to index. The key identifies a volume or normalized directory, the root directory is where an
///     enumeration walk starts, and the volume serial identifies the containing volume when the platform reports one.
/// </summary>
public sealed partial record IndexedRoot
{
    internal IndexedRoot(RootKey drive, string rootDirectory, uint volumeSerial,
        ProducerKind producer = ProducerKind.Mft)
    {
        Drive = drive;
        RootDirectory = rootDirectory;
        VolumeSerial = volumeSerial;
        Producer = producer;
        ValidateRootProducer(drive, producer);
    }

    /// <summary>The index root identity; assigning the default key throws <see cref="ArgumentException" />.</summary>
    public RootKey Drive { get; init => field = value.Validated(); }

    /// <summary>
    ///     Root directory where an enumeration walk starts; assigning null throws
    ///     <see cref="ArgumentNullException" /> and empty or whitespace throws <see cref="ArgumentException" />.
    /// </summary>
    public string RootDirectory { get; init => field = ValidateRootDirectory(value); }

    /// <summary>The serial of the containing volume, or zero when the platform does not report one.</summary>
    public uint VolumeSerial { get; init; }

    /// <summary>
    ///     Which producer builds this drive's block; assigning the default or an undefined kind throws
    ///     <see cref="ArgumentOutOfRangeException" />. <see cref="ProducerKind.Mft" /> requires a volume root and uses
    ///     <see cref="FileIndexOptions.MftSource" />. <see cref="ProducerKind.Enumeration" /> walks the directory tree,
    ///     ignoring <see cref="FileIndexOptions.MftSource" /> entirely, and is never selected on a caller's behalf.
    /// </summary>
    public ProducerKind Producer { get; init => field = ValidateProducer(value); }

    internal IndexedRoot Validated()
    {
        _ = Drive.Validated();
        _ = ValidateRootDirectory(RootDirectory);
        _ = ValidateProducer(Producer);
        ValidateRootProducer(Drive, Producer);
        return this;
    }

    static void ValidateRootProducer(RootKey drive, ProducerKind producer)
    {
        if (producer == ProducerKind.Mft && !drive.IsVolume)
        {
            throw new ArgumentException("An MFT producer requires a volume root.", nameof(drive));
        }
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
