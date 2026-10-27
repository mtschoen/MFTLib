using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Builds an <see cref="IndexedRoot" /> with test-controlled identity and volume serial.</summary>
public static class SyntheticIndexedRoot
{
    /// <summary>Forwards to the library's internal constructor for scenarios that need arbitrary block identity.</summary>
    public static IndexedRoot Create(RootKey root, string rootDirectory, uint volumeSerial,
        ProducerKind producer = ProducerKind.Mft) => new(root, rootDirectory, volumeSerial, producer);
}
