namespace MFTLib.Index;

/// <summary>
///     What an open resolved from its options before any cache lookup: the root, the volume under it, the producer
///     the selection picked and, when the MFT was ruled out, why. Immutable; a fallback to a walk replaces it with
///     <see cref="AsWalk" /> before the index is published to any caller.
/// </summary>
sealed record ResolvedRoot
{
    /// <summary>The root the options named, which is what <see cref="RootStatus.Root" /> reports.</summary>
    public required RootKey Root { get; init; }

    /// <summary>
    ///     The normalized host path of the root: the volume root for a volume key, the directory for a directory key,
    ///     the virtual dump root for a dump.
    /// </summary>
    public required string RootDirectory { get; init; }

    /// <summary>The lettered volume under the root, or null when it has none.</summary>
    public VolumeKey? Volume { get; init; }

    /// <summary>The host directory the volume is rooted at, or null with no <see cref="Volume" />.</summary>
    public string? VolumeRootDirectory { get; init; }

    /// <summary>The volume serial, or zero when the platform reports none.</summary>
    public uint VolumeSerial { get; init; }

    /// <summary>The selected producer, fixed for the index's life.</summary>
    public required ProducerKind Producer { get; init; }

    /// <summary>Why the MFT cannot serve this root; null when it can, or when a walk was asked for.</summary>
    public MftUnavailableReason? MftUnavailable { get; init; }

    /// <summary>True for a dump source, whose single virtual volume is never cached or watched.</summary>
    public bool IsDump { get; init; }

    /// <summary>
    ///     The root key stored in the block header and named by the cache file: the volume for an MFT block, whatever
    ///     the view's root, and the root itself for a walk.
    /// </summary>
    public RootKey BlockRoot => Producer == ProducerKind.Mft && Volume is { } volume ? volume : Root;

    /// <summary>The root that entry paths render under: the volume root for an MFT block, the walked root otherwise.</summary>
    public string RenderRootDirectory => Producer == ProducerKind.Mft ? VolumeRootDirectory ?? RootDirectory : RootDirectory;

    /// <summary>The same root served by a walk because the MFT could not be used for <paramref name="reason" />.</summary>
    public ResolvedRoot AsWalk(MftUnavailableReason reason) =>
        this with { Producer = ProducerKind.Enumeration, MftUnavailable = reason };
}
