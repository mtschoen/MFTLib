namespace MFTLib.Tests;

static class JournalEntryFactory
{
    public static UsnJournalEntry Create(
        ulong recordNumber,
        long usn,
        string fileName,
        UsnReason reason = UsnReason.Close,
        FileAttributes fileAttributes = FileAttributes.Normal) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = 5,
            Usn = usn,
            Timestamp = DateTime.UnixEpoch,
            Reason = reason,
            FileAttributes = fileAttributes,
            FileName = fileName,
        });
}
