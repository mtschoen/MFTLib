namespace MFTLib.Tests.TestSupport;

internal static class JournalMutatorDeduplicationTestSupport
{
    public static UsnJournalEntry CreateEntry(ulong recordNumber, ulong parentRecordNumber, string fileName,
        UsnReason reason, DateTime timestamp, ushort sequenceNumber = 0,
        FileAttributes fileAttributes = FileAttributes.Archive)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            SequenceNumber = sequenceNumber,
            Usn = 1000,
            Timestamp = timestamp,
            Reason = reason,
            FileAttributes = fileAttributes,
            FileName = fileName
        });
    }
}
