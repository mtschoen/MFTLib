using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Opens a client-created named block section and completes the broker's cold scan in it.</summary>
sealed class RealBlockSectionWriter(TimeProvider? timeProvider = null) : IBlockSectionWriter
{
    /// <summary>Opens the named section, which only Windows supports, and writes the scan into it.</summary>
    [SupportedOSPlatform("windows")]
    public BlockWriteResult Write(
        string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftScanOptions scanOptions, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        using var block = NamedBlockSection.OpenExisting(sectionName);
        return MftBlockScan.WriteToBlock(block, new BlockStamp(cursor, timeProvider ?? TimeProvider.System), batches, scanOptions,
            reporting, cancellationToken);
    }
}
