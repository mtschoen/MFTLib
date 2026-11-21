using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     How <see cref="InlineContentPlan" /> decides an entry from the block alone: an entry whose file incarnation was
///     replaced in its row, and a row that is delete-pending, are answered without a record read.
/// </summary>
[TestClass]
public sealed class InlineContentPlanTests : IDisposable
{
    const uint DirectoryRow = 6;
    const uint ChildRow = 7;
    const ushort ChildSequence = 20;
    const ushort ReplacementSequence = 21;

    static readonly DateTime Moment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    readonly TempDirectory _directory = new("mftlib-inline-plan");

    public void Dispose() => _directory.Dispose();

    static UsnJournalEntry Record(UsnReason reason, string name, ushort sequence) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = ChildRow,
            ParentRecordNumber = DirectoryRow,
            FileName = name,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            SequenceNumber = sequence,
            Usn = 1000,
            TimestampUtc = Moment
        });

    [TestMethod]
    public async Task Create_EntryWhoseRowNowHoldsAnotherIncarnation_IsSupersededAndNeverRead()
    {
        await using var fixture = new MutatorFixture();
        fixture.Block.SequenceNumbers[(int)ChildRow] = ChildSequence;
        var retained = FileEntry.Create(fixture.Snapshot[0], ChildRow);
        fixture.Apply(
        [
            Record(UsnReason.FileDelete | UsnReason.Close, "notes.txt", ChildSequence),
            Record(UsnReason.FileCreate | UsnReason.Close, "reused.txt", ReplacementSequence)
        ]);
        var replacement = FileEntry.Create(fixture.Snapshot[0], ChildRow);
        Assert.IsFalse(replacement.IsDeleted, "the row is live, so only its sequence number tells the incarnations apart");

        var plan = InlineContentPlan.Create(PinnedRootBlock.Current(fixture.Snapshot[0]), [retained, replacement], CancellationToken.None);

        CollectionAssert.AreEqual(new[] { (0, InlineContentOutcome.Superseded) }, plan.Decided);
        CollectionAssert.AreEqual(new[] { new InlineContentRequest(1, ChildRow, ReplacementSequence) }, plan.Requests,
            "the current incarnation is read with its own sequence number");
    }

    // A completed block of a root directory and one file, owned by whoever builds a root block over it.
    BlockFile BuildBlock()
    {
        var block = TestBlocks.Create(Path.Combine(_directory.Path, "T-00000001.mlix"), ProducerKind.Mft,
            slotCapacity: 8, namePoolCapacity: 1024, volumeSerial: 1);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, Moment.Ticks, 0));
        writer.TryWriteRow(1, "a.txt", new RowColumns(0, RowFlags.InUse, 32, 0, Moment.Ticks, 0));
        writer.Complete(Moment, null);
        return block;
    }

    [TestMethod]
    public async Task Create_RowThatIsDeletePending_IsSupersededAndNeverRead()
    {
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), BuildBlock(), rootDirectoryPath: TestRootDirectory.For('T'))];
        var writer = new BlockWriter(snapshot[0].Block);
        try
        {
            var entry = FileEntry.Create(snapshot[0], 1);
            Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(1, @"dir\a.txt"));

            var plan = InlineContentPlan.Create(PinnedRootBlock.Current(snapshot[0]), [entry], CancellationToken.None);

            CollectionAssert.AreEqual(new[] { (0, InlineContentOutcome.Superseded) }, plan.Decided);
            Assert.IsEmpty(plan.Requests);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
