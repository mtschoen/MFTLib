namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>The root the options named, which is what <see cref="RootStatus.Root" /> reports.</summary>
        public RootKey Root => Key is null ? Resolved.Root : Resolved.BlockRoot;

        /// <summary>
        ///     The key the block header stores and the cache file is named for: the volume for an MFT block, whatever the
        ///     view's root, so every view of a volume shares one block; the root itself for a walk.
        /// </summary>
        RootKey BlockRoot => Resolved.BlockRoot;

        /// <summary>The normalized host path of the root, which nesting between members of a set is judged by.</summary>
        public string RootDirectory => Resolved.RootDirectory;

        /// <summary>
        ///     True when this root is read from the MFT block of <paramref name="volume" />: a volume root, or a directory
        ///     view of it. Journal records are addressed by volume, so a view receives its volume's batches.
        /// </summary>
        public bool ServesVolume(VolumeKey volume) =>
            Resolved is { IsDump: false, Producer: ProducerKind.Mft, Volume: { } own } && own == volume;

        /// <summary>The path rules of the selected producer's blocks.</summary>
        public RootKind Kind => RootKind.For(Resolved.Producer, Resolved.IsDump);

        /// <summary>The root's own path in the form <c>LookupEngine.Find</c> matches, which a directory view scopes to.</summary>
        public string? MatchableRootDirectoryPath => Kind.ToMatchablePrefix(Kind.NormalizeRoot(Resolved.RootDirectory));

        /// <summary>The lettered volume the MFT is read from; walks and unlettered roots have none.</summary>
        public VolumeKey Volume => Resolved.Volume
            ?? throw new InvalidOperationException($"Root {Root} has no lettered volume.");

        /// <summary>
        ///     Why the last scan attempt could not use the MFT (a failed broker launch or elevation), which
        ///     <see cref="RootStatus.MftUnavailable" /> reports until a publication clears it. Guarded by the state lock.
        /// </summary>
        MftUnavailableReason? _lastMftFailure;
    }
}
