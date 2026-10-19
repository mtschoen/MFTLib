using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>The scan shapes a test block carries so an index opened with default row-shaping options adopts it.</summary>
internal static class SyntheticScanShape
{
    /// <summary>
    ///     The shape an index with null retention, default walk options and default MFT scan options requires. An
    ///     enumeration shape with no pruned paths does not depend on the drive's root, so any absolute directory serves.
    /// </summary>
    /// <param name="producerKind">The producer recorded in the block's header.</param>
    /// <returns>The default shape for that producer.</returns>
    internal static ulong Default(ProducerKind producerKind) => producerKind == ProducerKind.Enumeration
        ? ScanShape.ForEnumeration(null, new DirectoryWalkOptions(), Path.GetTempPath())
        : ScanShape.ForMft(null, includeFreed: false);
}
