using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953, name compaction audit: the journal apply reports what its writer wrote. Every change path, and
///     the incarnation of every change's handle, is read on the writer's block, never on whatever mapping the lineage
///     publishes as current, so a change can never pair a row of one mapping with the names of another.
/// </summary>
[TestClass]
public sealed class JournalMutatorMappingTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
    static readonly RowColumns FileColumns = new(0, RowFlags.InUse, 32, 0, Moment.Ticks, 0);

    readonly TempDirectory _directory = new("mftlib-mutator-mapping");

    public void Dispose() => _directory.Dispose();

    static UsnJournalEntry Rename(ulong record, string name, long usn) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = 0,
            Usn = usn,
            TimestampUtc = Moment,
            Reason = UsnReason.RenameNewName | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = name
        });

    [TestMethod]
    public void ChangePaths_AreReadOnTheWritersBlock_NotTheLineagesCurrentMapping()
    {
        // The lineage releases both blocks; disposing them again afterwards is a no-op.
        using var written = TestBlocks.Create(Path.Combine(_directory.Path, "T-0000000000000001.mlix"), ProducerKind.Mft,
            slotCapacity: 8, namePoolCapacity: 1024, volumeSerial: 1);
        var writer = new BlockWriter(written);
        writer.TryWriteRow(0, "", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, Moment.Ticks, 0));
        writer.TryWriteRow(1, "old.txt", FileColumns);
        writer.TryRenameRow(1, "a.txt", 0);
        writer.Complete(Moment, null);
        var rootBlock = new RootBlock(new VolumeKey('T'), written, rootDirectoryPath: TestRootDirectory.For('T'));
        using var other = TestBlocks.Create(Path.Combine(_directory.Path, "T-0000000000000001.mlix.other"), ProducerKind.Mft,
            slotCapacity: 8, namePoolCapacity: 1024, volumeSerial: 1);
        try
        {
            using (var references = NameReferences.Count(written))
            {
                BlockCopier.CopyCompacting(written, other, references, CancellationToken.None);
            }

            rootBlock.Mapped.Replace(other);

            var result = new JournalMutator(writer).ApplyUntilRefusal(rootBlock, [Rename(1, "c.txt", 100)], 0, 9, 200);

            var change = result.Changes.Single();
            Assert.AreEqual(FileChangeKind.Renamed, change.Kind);
            Assert.EndsWith("c.txt", change.Path);
            Assert.EndsWith("a.txt", change.PreviousPath!);
        }
        finally
        {
            rootBlock.Mapped.ReleaseUnborrowed();
        }
    }
}
