namespace MFTLib.Index;

/// <summary>
///     What makes an <see cref="MftIndexSource" /> a dump: the file it loads and the logical volume
///     key the index assigns it. The index treats a block from such a source as a virtual
///     inventory and never as the live volume that happens to share the letter.
/// </summary>
sealed record MftDumpSourceIdentity
{
    internal MftDumpSourceIdentity(string dumpFilePath, VolumeKey volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFilePath);
        DumpFilePath = Path.GetFullPath(dumpFilePath);
        Volume = volume.Validated();
    }

    /// <summary>The full path of the dump file the source loads, resolved against the current directory once.</summary>
    internal string DumpFilePath { get; }

    /// <summary>The letter the index files the dump under, in upper case; it names no live volume.</summary>
    internal VolumeKey Volume { get; }

    /// <summary>The prefix of every path this dump renders, which no host path can start with.</summary>
    internal string Root => MftDumpPaths.CanonicalRoot(Volume);

    /// <summary>
    ///     Checks that the options leave every volume and cache fact to the dump source, before any
    ///     file or cache write.
    /// </summary>
    /// <exception cref="ArgumentException">An option the dump source supplies itself is set.</exception>
    internal static void ValidateOptions(FileIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Producer == ProducerSelection.WalkOnly)
        {
            throw new ArgumentException(
                "A dump source is an MFT inventory, so FileIndexOptions.Producer cannot be WalkOnly.", nameof(options));
        }

        if (options.Root is not null)
        {
            throw new ArgumentException("A dump source supplies its own volume, so FileIndexOptions.Root must be unset.",
                nameof(options));
        }

        if (options.CacheMode is not null)
        {
            throw new ArgumentException(
                "A dump source is never cached, so FileIndexOptions.CacheMode must be unset, Disabled included.",
                nameof(options));
        }

        if (options.CacheDirectory is not null)
        {
            throw new ArgumentException("A dump source is never cached, so FileIndexOptions.CacheDirectory must be unset.",
                nameof(options));
        }

        if (options.CacheTag != default)
        {
            throw new ArgumentException("A dump source is never cached, so FileIndexOptions.CacheTag must be unset.",
                nameof(options));
        }
    }
}
