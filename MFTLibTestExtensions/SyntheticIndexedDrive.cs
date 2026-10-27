using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Builds an <see cref="IndexedRoot" /> with test-controlled identity and volume serial.</summary>
public static class SyntheticIndexedDrive
{
    /// <summary>Forwards to the library's internal constructor for scenarios that need arbitrary block identity.</summary>
    public static IndexedRoot Create(RootKey drive, string rootDirectory, uint volumeSerial,
        ProducerKind producer = ProducerKind.Mft) => new(drive, rootDirectory, volumeSerial, producer);
}
