using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalEntryTests
{
    [TestMethod]
    public void SequenceNumber_RoundTripsThroughCreate()
    {
        var entry = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 1197729,
            ParentRecordNumber = 5,
            SequenceNumber = 169,
            Usn = 42,
            TimestampUtc = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
            Reason = UsnReason.FileCreate,
            FileAttributes = FileAttributes.Normal,
            FileName = "probe.txt"
        });

        Assert.AreEqual((ushort)169, entry.SequenceNumber);
        Assert.AreEqual(1197729ul, entry.RecordNumber);
    }
}
