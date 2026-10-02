using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

public partial class JournalMutatorTests
{
    [TestMethod]
    [DataRow(6)]
    [DataRow(1)]
    public void CreateCycle_DataWriteInsideCreateCycle_ReportsOneCreatedOneModifiedOneDeleted(int recordsPerBatch)
    {
        UsnJournalEntry[] records =
        [
            CreateEntry(5, 1, "notes.txt", UsnReason.FileCreate, ChangeMoment),
            CreateEntry(5, 1, "notes.txt", UsnReason.DataExtend | UsnReason.FileCreate, ChangeMoment),
            CreateEntry(5, 1, "notes.txt", UsnReason.DataExtend | UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
            CreateEntry(5, 1, "notes.txt", UsnReason.DataExtend, ChangeMoment),
            CreateEntry(5, 1, "notes.txt", UsnReason.DataExtend | UsnReason.Close, ChangeMoment),
            CreateEntry(5, 1, "notes.txt", UsnReason.FileDelete | UsnReason.Close, ChangeMoment)
        ];
        var mutator = new JournalMutator(_writer);
        var changes = new List<FileChange>();
        for (var start = 0; start < records.Length; start += recordsPerBatch)
        {
            var batch = records.Skip(start).Take(recordsPerBatch).ToArray();
            changes.AddRange(mutator.Apply(_snapshot, 0, batch, journalId: 7, nextUsn: 1000 + start + batch.Length));
        }

        Assert.AreEqual(1, changes.Count(change => change.Kind == FileChangeKind.Created),
            "One real create cycle must report exactly one Created change.");
        Assert.AreEqual(1, changes.Count(change => change.Kind == FileChangeKind.Modified),
            "One real append cycle must report exactly one Modified change.");
        Assert.AreEqual(1, changes.Count(change => change.Kind == FileChangeKind.Deleted),
            "One real delete must report exactly one Deleted change.");
        Assert.AreEqual(3, changes.Count,
            "Total changes must be exactly three: one Created, one Modified, one Deleted.");
        CollectionAssert.AreEqual(
            new[] { FileChangeKind.Created, FileChangeKind.Modified, FileChangeKind.Deleted },
            changes.Select(change => change.Kind).ToArray());
    }
}
