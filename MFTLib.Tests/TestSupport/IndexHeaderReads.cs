using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>Reads the block header an open index holds for a drive, the way a consumer test does.</summary>
internal static class IndexHeaderReads
{
    /// <summary>The header of the drive's current block; fails the test when the drive has none.</summary>
    internal static SyntheticDriveHeader HeaderOf(this FileIndex index, char driveLetter = 'T') =>
        SyntheticIndexInspection.ReadHeader(index, driveLetter)
        ?? throw new AssertFailedException($"Drive {driveLetter} has no block, so it has no header.");
}
