using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A synthetic volume for an index open: a root key mapped to a host directory with a serial, read through the
///     MFT (<see cref="ProducerKind.Mft" />, selected as MftOnly) or walked (<see cref="ProducerKind.Enumeration" />, WalkOnly).
///     Applying it routes the open through <see cref="SyntheticVolumeFacts.Apply" /> so no host drive is consulted.
/// </summary>
/// <param name="Root">The root key the index opens; a volume key is mapped to <paramref name="RootDirectory" />.</param>
/// <param name="RootDirectory">The host directory the synthetic volume is rooted at.</param>
/// <param name="VolumeSerial">The serial the synthetic volume reports.</param>
/// <param name="Producer">Which producer the open asks for.</param>
sealed record SyntheticRoot(RootKey Root, string RootDirectory, uint VolumeSerial, ProducerKind Producer)
{
    public static SyntheticRoot Create(RootKey root, string rootDirectory, uint volumeSerial,
        ProducerKind producer = ProducerKind.Mft) => new(root, rootDirectory, volumeSerial, producer);

    public ProducerSelection Selection => SelectionFor(Producer);

    /// <summary>The selection that makes an open use exactly <paramref name="producer" />.</summary>
    public static ProducerSelection SelectionFor(ProducerKind producer) =>
        producer == ProducerKind.Mft ? ProducerSelection.MftOnly : ProducerSelection.WalkOnly;

    public FileIndexOptions Apply(FileIndexOptions options)
    {
        var selected = options with { Root = Root, Producer = Selection };
        return Root.TryGetVolume(out var volume)
            ? SyntheticVolumeFacts.Apply(selected, VolumeSerial, volume: volume, volumeRootDirectory: RootDirectory)
            : SyntheticVolumeFacts.Apply(selected, VolumeSerial);
    }
}

static class SyntheticRootExtensions
{
    public static FileIndexOptions WithSyntheticRoot(this FileIndexOptions options, SyntheticRoot root) =>
        root.Apply(options);
}
