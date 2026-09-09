using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

public sealed class RecordingBlockSectionWriter(Func<string, BlockFile?>? resolveSection = null,
    DateTime? completionTimestamp = null) : IBlockSectionWriter, IDisposable
{
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
        MftBlockRowFilter filter, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        LastSectionName = sectionName;
        LastFilter = filter;
        var writer = new BlockWriter(resolveSection?.Invoke(sectionName) ?? Block);
        var result = MftBlockRowWriter.WriteBatches(writer, batches, filter, progress, cancellationToken);
        writer.SetJournalCursor(cursor.JournalId, cursor.NextUsn);
        writer.Complete(completionTimestamp ?? new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc));
        return result;
    }

    public void Dispose() => Block.Dispose();
}
