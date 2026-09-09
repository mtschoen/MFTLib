using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Opens a client-created named block section and completes the broker's cold scan in it.</summary>
[SupportedOSPlatform("windows")]
public sealed class RealBlockSectionWriter : IBlockSectionWriter
{
    public BlockWriteResult Write(
        string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        using var block = NamedBlockSection.OpenExisting(sectionName);
        var writer = new BlockWriter(block);
        var result = MftBlockRowWriter.WriteBatches(writer, batches, filter, progress, cancellationToken);
        writer.SetJournalCursor(cursor.JournalId, cursor.NextUsn);
        writer.Complete(DateTime.UtcNow);
        return result;
    }
}
