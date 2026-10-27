using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Builds an <see cref="IndexedDrive" /> with test-controlled identity and volume serial.</summary>
public static class SyntheticIndexedDrive
{
    /// <summary>Forwards to the library's internal constructor for scenarios that need arbitrary block identity.</summary>
    public static IndexedDrive Create(RootKey drive, string rootDirectory, uint volumeSerial,
        ProducerKind producer = ProducerKind.Mft) => new(drive, rootDirectory, volumeSerial, producer);
}
