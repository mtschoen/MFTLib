using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Opens a client-created named block section and completes the broker's cold scan in it.</summary>
[SupportedOSPlatform("windows")]
public sealed class RealBlockSectionWriter : IBlockSectionWriter
{
    // The processing step each flushed range of the completed block publishes.
    internal const string FlushStep = "block flush";

    public BlockWriteResult Write(
        string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        using var block = NamedBlockSection.OpenExisting(sectionName);
        var writer = new BlockWriter(block);
        var result = MftBlockRowWriter.WriteBatches(writer, batches, filter, reporting.Progress, cancellationToken);
        writer.SetJournalCursor(cursor.JournalId, cursor.NextUsn);
        var operation = reporting.Operation;
        writer.Complete(DateTime.UtcNow, operation is null ? null : _ => operation.Processing(FlushStep));
        return result;
    }
}
