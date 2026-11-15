using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Regression tests for the four defects that made stopping at a refused journal record unsafe: a refusal must
///     leave the block exactly as it was, so the record can be applied whole later.
/// </summary>
[TestClass]
public sealed class JournalMutatorRefusalDefectTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly RowColumns DirectoryColumns = new(ParentRow: 0, RowFlags.InUse | RowFlags.Directory,
        Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 0);
    static readonly RowColumns FileColumns = new(ParentRow: 0, RowFlags.InUse, Attributes: 32, Size: 0,
        Moment.Ticks, SequenceNumber: 0);

    TempDirectory _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = new TempDirectory("mftlib-refusal-defects");

    public void Dispose() => _directory.Dispose();

    (BlockFile Block, BlockWriter Writer, RootBlock[] Snapshot) BuildRoot(uint namePoolCapacity,
        uint? deltaCapacity = null)
    {
        var block = TestBlocks.Create(Path.Combine(_directory.Path, "T-00000001.mlix"), ProducerKind.Mft,
            slotCapacity: 8, namePoolCapacity, volumeSerial: 1, deltaCapacity: deltaCapacity);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", DirectoryColumns);
        writer.TryWriteRow(1, "a", FileColumns);
        writer.Complete(Moment, null);
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'))];
        return (block, writer, snapshot);
    }

    static UsnJournalEntry Entry(ulong recordNumber, string fileName, UsnReason reason, long usn = 1,
        FileAttributes attributes = FileAttributes.Archive)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = 0,
            Usn = usn,
            TimestampUtc = Moment,
            Reason = reason,
            FileAttributes = attributes,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task RefusedRename_DoesNotFallThroughToAMetadataUpdate()
    {
        var (block, writer, snapshot) = BuildRoot(namePoolCapacity: 1);
        try
        {
            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Entry(1, "much-longer-name.txt", UsnReason.RenameNewName,
                    attributes: FileAttributes.Archive | FileAttributes.ReadOnly)], 0,
                journalIdentifier: 1, nextUsn: 10);

            Assert.IsEmpty(result.Changes);
            Assert.AreEqual(WriteRefusal.NamePool, result.Refusal?.Reason);
            Assert.AreEqual(32u, block.Rows[1].Attributes);
            Assert.AreEqual("a", NamePool.ReadRowName(block, 1).ToString());
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task RefusedRecord_IsNotMarkedAsReportedInItsCycle()
    {
        var (_, writer, snapshot) = BuildRoot(namePoolCapacity: 1);
        try
        {
            new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Entry(1, "much-longer-name.txt", UsnReason.RenameNewName)], 0, journalIdentifier: 1, nextUsn: 10);

            Assert.AreEqual(UsnReason.None, snapshot[0].ReportedCycles.GetReportedReasons(1, 0));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task RowWriteRefusedByAFullDelta_InternsNoNameEntry()
    {
        var (block, writer, snapshot) = BuildRoot(namePoolCapacity: 512, deltaCapacity: 1);
        try
        {
            writer.TryWriteRow(2, "consumes-the-only-delta-entry", FileColumns);
            var entriesBefore = block.NameEntryCount;
            var poolBefore = block.NamePoolUsed;

            Assert.AreEqual(WriteRefusal.Delta, writer.TryWriteRow(3, "refused-by-the-delta", FileColumns));

            Assert.AreEqual(entriesBefore, block.NameEntryCount);
            Assert.AreEqual(poolBefore, block.NamePoolUsed);
            Assert.IsFalse(block.Rows[3].IsInUse);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task TombstoneOfADeletePendingRow_WhoseLastSegmentCannotBeStored_ChangesNothing()
    {
        // Room for the one-byte "a" and the thirteen-byte pending path, not for the nine-byte "other.txt".
        var (block, writer, snapshot) = BuildRoot(namePoolCapacity: 14);
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(1, @"dir\other.txt"));
            var nameBefore = NamePool.ReadRowName(block, 1).ToString();

            Assert.AreEqual(WriteRefusal.NamePool, writer.MarkTombstone(1));

            var flags = writer.ReadRowFlags(1);
            Assert.AreEqual(RowFlags.None, flags & RowFlags.Tombstone);
            Assert.AreEqual(RowFlags.DeletePending, flags & RowFlags.DeletePending);
            Assert.AreEqual(nameBefore, NamePool.ReadRowName(block, 1).ToString());
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
