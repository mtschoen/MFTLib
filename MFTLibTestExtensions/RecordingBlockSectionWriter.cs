using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Writes each scan through the production row writer into the section the client created, and
///     remembers the last section and retained file names.
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
        NamePoolCapacity = 4096,
        DeleteOnClose = true
    });

    public string? LastSectionName { get; private set; }
    public IReadOnlyCollection<string>? LastRetainedFileNames { get; private set; }

    public BlockWriteResult Write(string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftRowSelection selection, BlockWriteReporting reporting, CancellationToken cancellationToken)
    {
        LastSectionName = sectionName;
        LastRetainedFileNames = selection.RetainedFileNames;
        return MftBlockScan.WriteToBlock(resolveSection?.Invoke(sectionName) ?? Block,
            new BlockStamp(cursor, timeProvider ?? TimeProvider.System), batches, selection, reporting with { Operation = null },
            cancellationToken);
    }

    public void Dispose() => Block.Dispose();
}
