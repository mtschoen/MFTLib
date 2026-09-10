using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The NTFS record sequence number a journal entry carries has to reach the row, because
///     <see cref="FileEntry.Open" /> composes the NTFS file reference from it and a zero there
///     names a reference the operating system rejects.
/// </summary>
[TestClass]
public class JournalMutatorSequenceNumberTests
{
    [TestMethod]
    public void Create_StampsTheEntrysSequenceNumberOnTheRowItWrites()
    {
        using var fixture = new MutatorFixture();
        var created = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 30,
            ParentRecordNumber = 6,
            FileName = "created.cs",
            Reason = UsnReason.FileCreate,
            FileAttributes = FileAttributes.Archive,
            SequenceNumber = 9,
            Usn = 1000,
            Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([created]).Single();

        Assert.AreEqual(FileChangeKind.Created, change.Kind);
        Assert.AreEqual((ushort)9, fixture.Block.SequenceNumbers[30]);
    }
}
