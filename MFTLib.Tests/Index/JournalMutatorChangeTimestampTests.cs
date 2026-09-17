using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Every <see cref="FileChange" /> carries the timestamp of the USN journal record that
///     produced it, captured when the mutation was applied. The row's own ModifiedTicks keeps
///     moving while the batch runs (a coalesced close record restamps it last), so a consumer
///     reading the row inside the notification cannot recover the originating record's time;
///     the change carries it instead. A delete's timestamp lives on the change, not the
///     tombstoned row, so it survives the delete.
/// </summary>
[TestClass]
public class JournalMutatorChangeTimestampTests
{
    static UsnJournalEntry Entry(ulong recordNumber, ulong parentRecordNumber, string fileName,
        UsnReason reason, DateTime timestamp)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            SequenceNumber = 1,
            Usn = 1000,
            Timestamp = timestamp,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task OneBatch_EachChangeCarriesItsOriginatingRecordTimestamp()
    {
        await using var fixture = new MutatorFixture();
        var extendMoment = fixture.Timestamp;
        var infoMoment = fixture.Timestamp.AddSeconds(10);
        var deleteMoment = fixture.Timestamp.AddSeconds(20);
        var closeMoment = fixture.Timestamp.AddMinutes(1);

        // One batch: two modification records against row 7 (distinct reason bits, so the
        // second is not the close-coalesced echo of the first), a delete of a row the index
        // never had (hydrated, then tombstoned), and a close record that repeats row 7's
        // reported reasons and so emits no change but still restamps the row.
        var changes = fixture.Apply(
        [
            Entry(7, 6, "notes.txt", UsnReason.DataExtend, extendMoment),
            Entry(7, 6, "notes.txt", UsnReason.BasicInfoChange, infoMoment),
            Entry(9, 6, "doomed.txt", UsnReason.FileDelete, deleteMoment),
            Entry(7, 6, "notes.txt", UsnReason.DataExtend | UsnReason.BasicInfoChange | UsnReason.Close,
                closeMoment)
        ]);

        Assert.AreEqual(3, changes.Count, "The suppressed close record emits no change.");
        CollectionAssert.AreEqual(
            new[] { FileChangeKind.Modified, FileChangeKind.Modified, FileChangeKind.Deleted },
            changes.Select(change => change.Kind).ToArray());

        // Each change reports the time of its own record, not the batch's final row value.
        Assert.AreEqual(extendMoment, changes[0].Timestamp);
        Assert.AreEqual(infoMoment, changes[1].Timestamp);

        // The close record's metadata still landed on the row, which is exactly why the row
        // cannot be the source of the change's time.
        Assert.AreEqual(closeMoment.Ticks, fixture.Block.Rows[7].ModifiedTicks);

        // The delete's timestamp survives the tombstone because it lives on the change.
        Assert.IsTrue(fixture.Block.Rows[9].IsDeleted);
        Assert.AreEqual(deleteMoment, changes[2].Timestamp);
    }

    [TestMethod]
    public async Task CreateAndRename_CarryTheirOwnRecordTimestamps()
    {
        await using var fixture = new MutatorFixture();
        var createMoment = fixture.Timestamp;
        var renameMoment = fixture.Timestamp.AddSeconds(30);

        var changes = fixture.Apply(
        [
            Entry(9, 6, "fresh.txt", UsnReason.FileCreate, createMoment),
            Entry(9, 6, "renamed.txt", UsnReason.RenameNewName, renameMoment)
        ]);

        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
        Assert.AreEqual(createMoment, changes[0].Timestamp);
        Assert.AreEqual(FileChangeKind.Renamed, changes[1].Kind);
        Assert.AreEqual(renameMoment, changes[1].Timestamp);
        Assert.AreEqual(renameMoment.Ticks, fixture.Block.Rows[9].ModifiedTicks);
    }
}
