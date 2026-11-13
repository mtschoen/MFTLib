using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Writes each scan through the production row writer into the section the client created, and
///     remembers the last section.
/// </summary>
sealed class RecordingBlockSectionWriter(Func<string, BlockFile?>? resolveSection = null,
    TimeProvider? timeProvider = null) : IBlockSectionWriter, IDisposable
{
    public BlockFile Block { get; } = BlockFile.Create(new BlockFileCreateOptions
    {
        Path = Path.Combine(Path.GetTempPath(), $"mft-block-section-{Guid.NewGuid():N}.bin"),
        Root = new VolumeKey('C'),
        VolumeSerial = 123,
        ProducerKind = ProducerKind.Mft,
        RootRow = 5,
        SlotCapacity = 256,
        NameEntryCapacity = 256,
        NamePoolCapacity = 4096,
        DeltaCapacity = BlockLayout.ComputeDeltaCapacity(256),
        DeleteOnClose = true
    });

    public string? LastSectionName { get; private set; }

    public BlockWriteResult Write(BlockSectionTarget target, UsnJournalCursor cursor, IMftSliceSource source,
        MftScanOptions scanOptions, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        LastSectionName = target.SectionName;
        return MftBlockScan.WriteToBlock(resolveSection?.Invoke(target.SectionName) ?? Block,
            new BlockStamp(cursor, timeProvider ?? TimeProvider.System), source, scanOptions, reporting with { Operation = null }, cancellationToken);
    }

    public void Dispose() => Block.Dispose();
}
