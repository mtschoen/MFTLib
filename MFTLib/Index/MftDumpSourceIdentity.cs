namespace MFTLib.Index;

/// <summary>
///     What makes an <see cref="MftIndexSource" /> a dump: the file it loads and the logical drive
///     key the index assigns it. The index treats a block from such a source as a virtual
///     inventory and never as the live volume that happens to share the letter.
/// </summary>
internal sealed record MftDumpSourceIdentity
{
    internal MftDumpSourceIdentity(string dumpFilePath, DriveKey driveLetter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFilePath);
        DumpFilePath = Path.GetFullPath(dumpFilePath);
        Drive = driveLetter.Validated();
    }

    /// <summary>The full path of the dump file the source loads, resolved against the current directory once.</summary>
    internal string DumpFilePath { get; }

    /// <summary>The letter the index files the dump under, in upper case; it names no live volume.</summary>
    internal DriveKey Drive { get; }

    /// <summary>The prefix of every path this dump renders, which no host path can start with.</summary>
    internal string Root => MftDumpPaths.CanonicalRoot(Drive);

    /// <summary>
    ///     Checks the options a dump index opens with, before any file or cache write.
    /// </summary>
    /// <exception cref="ArgumentException">An option is not one a dump source accepts.</exception>
    internal void ValidateOptions(FileIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Drives is not [{ } drive] || drive.Drive != Drive)
        {
            throw new ArgumentException("The dump source requires exactly its configured logical drive key.",
                nameof(options));
        }

        if (!MftDumpPaths.IsCanonicalRoot(drive.RootDirectory, Drive))
        {
            throw new ArgumentException($"A dump root must be {Root}.", nameof(options));
        }

        if (!options.NoCache || options.InitialOpenCacheOnly || options.CacheTag != default ||
            options.CacheDirectory is not null)
        {
            throw new ArgumentException("Dump sources require NoCache and do not accept cache options.",
                nameof(options));
        }

        if (drive.ProducerPolicy != ProducerPolicy.Mft)
        {
            throw new ArgumentException("Dump sources require ProducerPolicy.Mft.", nameof(options));
        }

        if (drive.VolumeSerial != 0)
        {
            throw new ArgumentException("A dump source requires VolumeSerial to be zero.", nameof(options));
        }
    }
}
