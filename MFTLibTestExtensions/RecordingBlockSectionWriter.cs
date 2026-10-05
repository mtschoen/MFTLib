using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Writes each scan through the production row writer into the section the client created, and
///     remembers the last section and filter.
/// </summary>
internal sealed class RecordingBlockSectionWriter(Func<string, BlockFile?>? resolveSection = null)
    : IBlockSectionWriter, IDisposable
{
    static readonly DateTime CompletedUtc = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    public BlockFile Block { get; } = BlockFile.Create(new BlockFileCreateOptions
    {
        Path = Path.Combine(Path.GetTempPath(), $"mft-block-section-{Guid.NewGuid():N}.bin"),
        VolumeSerial = 123,
        ProducerKind = ProducerKind.Mft,
        RootRow = 5,
        SlotCapacity = 256,
        NamePoolCapacity = 4096,
        DeleteOnClose = true
    });

    public string? LastSectionName { get; private set; }
    public MftBlockRowFilter LastFilter { get; private set; }

    public BlockWriteResult Write(string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftBlockRowFilter filter, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        LastSectionName = sectionName;
        LastFilter = filter;
        var writer = new BlockWriter(resolveSection?.Invoke(sectionName) ?? Block);
        var result = MftBlockRowWriter.WriteBatches(writer, batches, filter, reporting.Progress, cancellationToken);
        writer.SetJournalCursor(cursor.JournalId, cursor.NextUsn);
        writer.Complete(CompletedUtc, null);
        return result;
    }

    public void Dispose() => Block.Dispose();
}
