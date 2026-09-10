using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class JournalMutatorHydrationTests
{
    [TestMethod]
    public void Modification_HydratesAnUnwrittenRowAndStillReportsModified()
    {
        using var fixture = new MutatorFixture();          // row 20 was never written by the producer
        var edited = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 20,
            ParentRecordNumber = 6,
            FileName = "source.cs",
            Reason = UsnReason.DataOverwrite,
            FileAttributes = FileAttributes.Archive,
            SequenceNumber = 3,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([edited]).Single();

        Assert.AreEqual(FileChangeKind.Modified, change.Kind);
        Assert.AreEqual(@"T:\documents\source.cs", change.Path);
        Assert.IsTrue(change.Entry.SizeKnown == false);
        Assert.AreEqual((ushort)3, fixture.Block.SequenceNumbers[20]);
    }

    [TestMethod]
    public void Delete_HydratesAnUnwrittenRowSoTheTombstoneKeepsItsPath()
    {
        using var fixture = new MutatorFixture();
        var deleted = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 21,
            ParentRecordNumber = 6,
            FileName = "gone.cs",
            Reason = UsnReason.FileDelete,
            FileAttributes = FileAttributes.Archive,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([deleted]).Single();

        Assert.AreEqual(FileChangeKind.Deleted, change.Kind);
        Assert.AreEqual(@"T:\documents\gone.cs", change.Path);
        Assert.IsTrue(change.Entry.IsDeleted);
    }

    [TestMethod]
    public void Rename_HydratesAnUnwrittenRowAndReportsCreatedWithNoPreviousPath()
    {
        using var fixture = new MutatorFixture();
        var renamed = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 23,
            ParentRecordNumber = 6,
            FileName = "surfaced.cs",
            Reason = UsnReason.RenameNewName,
            FileAttributes = FileAttributes.Archive,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([renamed]).Single();

        Assert.AreEqual(FileChangeKind.Created, change.Kind);
        Assert.AreEqual(@"T:\documents\surfaced.cs", change.Path);
        Assert.IsNull(change.PreviousPath);
    }

    [TestMethod]
    public void Hydration_RefusesAnOutOfRangeParentAndMarksCompactionNeeded()
    {
        using var fixture = new MutatorFixture();
        var bad = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 22,
            ParentRecordNumber = (ulong)uint.MaxValue + 1,
            FileName = "orphan.cs",
            Reason = UsnReason.DataOverwrite,
            FileAttributes = FileAttributes.Archive,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        Assert.AreEqual(0, fixture.Apply([bad]).Count);
        Assert.IsTrue(fixture.Block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public void Delete_HydrationFailsForOutOfRangeParent_ReturnsNoChangeAndMarksCompactionNeeded()
    {
        using var fixture = new MutatorFixture();
        var bad = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 25,
            ParentRecordNumber = (ulong)uint.MaxValue + 1,
            FileName = "orphan-delete.cs",
            Reason = UsnReason.FileDelete,
            FileAttributes = FileAttributes.Archive,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        Assert.AreEqual(0, fixture.Apply([bad]).Count);
        Assert.IsTrue(fixture.Block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public void Rename_HydrationFailsForOutOfRangeParent_ReturnsNoChangeAndMarksCompactionNeeded()
    {
        using var fixture = new MutatorFixture();
        var bad = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 26,
            ParentRecordNumber = (ulong)uint.MaxValue + 1,
            FileName = "orphan-rename.cs",
            Reason = UsnReason.RenameNewName,
            FileAttributes = FileAttributes.Archive,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        Assert.AreEqual(0, fixture.Apply([bad]).Count);
        Assert.IsTrue(fixture.Block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public void Hydration_OfADirectoryRow_SetsTheDirectoryFlagRatherThanSizeUnknown()
    {
        using var fixture = new MutatorFixture();
        var edited = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 24,
            ParentRecordNumber = 6,
            FileName = "NewSubfolder",
            Reason = UsnReason.DataOverwrite,
            FileAttributes = FileAttributes.Directory,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([edited]).Single();

        Assert.AreEqual(FileChangeKind.Modified, change.Kind);
        Assert.IsTrue(fixture.Block.Rows[24].IsDirectory);
        Assert.IsTrue(fixture.Block.Rows[24].SizeKnown);
    }

    [TestMethod]
    public void Modification_OfALiveRowDoesNotRewriteItsName()
    {
        using var fixture = new MutatorFixture();
        var edited = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 7,
            ParentRecordNumber = 6,
            FileName = "different-name.txt",
            Reason = UsnReason.DataOverwrite,
            FileAttributes = FileAttributes.Archive,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([edited]).Single();

        Assert.AreEqual("notes.txt", change.Entry.Name);
    }
}
