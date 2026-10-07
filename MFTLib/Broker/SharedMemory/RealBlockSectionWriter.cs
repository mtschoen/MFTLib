using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Opens a client-created named block section and completes the broker's cold scan in it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RealBlockSectionWriter : IBlockSectionWriter
{
    public BlockWriteResult Write(
        string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        using var block = NamedBlockSection.OpenExisting(sectionName);
        return MftBlockScan.WriteToBlock(block, new BlockStamp(cursor, () => DateTime.UtcNow), batches, filter, reporting,
            cancellationToken);
    }
}
