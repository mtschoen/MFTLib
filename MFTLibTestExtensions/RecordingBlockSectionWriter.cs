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
        return MftBlockScan.WriteToBlock(resolveSection?.Invoke(sectionName) ?? Block,
            new BlockStamp(cursor, () => CompletedUtc), batches, filter, reporting with { Operation = null },
            cancellationToken);
    }

    public void Dispose() => Block.Dispose();
}
