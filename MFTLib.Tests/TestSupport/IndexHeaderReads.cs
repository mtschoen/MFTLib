using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>Reads the block header an open index holds for a root, the way a consumer test does.</summary>
static class IndexHeaderReads
{
    /// <summary>The header of the root's current block; fails the test when the root has none.</summary>
    internal static SyntheticRootHeader HeaderOf(this FileIndex index, char driveLetter = 'T') =>
        SyntheticIndexInspection.ReadHeader(index, new VolumeKey(driveLetter))
        ?? throw new AssertFailedException($"Root {driveLetter} has no block, so it has no header.");
}
