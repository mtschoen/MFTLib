using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>The scan shapes a test block carries so an index opened with default row-shaping options adopts it.</summary>
static class SyntheticScanShape
{
    /// <summary>
    ///     The shape an index with default walk options and default MFT scan options requires. An enumeration
    ///     shape with no pruned paths does not depend on the root directory, so any absolute directory serves.
    /// </summary>
    /// <param name="producerKind">The producer recorded in the block's header.</param>
    /// <returns>The default shape for that producer.</returns>
    internal static ulong Default(ProducerKind producerKind) => producerKind == ProducerKind.Enumeration
        ? ScanShape.ForEnumeration(new DirectoryWalkOptions(), Path.GetTempPath())
        : ScanShape.ForMft(includeFreed: false);
}
