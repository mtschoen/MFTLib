using MFTLibTestExtensions;

namespace MFTLib.Tests;

/// <summary>The terse form most tests want: a journal entry under the root directory.</summary>
static class JournalEntries
{
    const ulong RootRecordNumber = 5;

    public static UsnJournalEntry Create(
        ulong recordNumber,
        long usn,
        string fileName,
        UsnReason reason = UsnReason.Close,
        FileAttributes fileAttributes = FileAttributes.Normal)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = RootRecordNumber,
            Usn = usn,
            TimestampUtc = DateTime.UnixEpoch,
            FileName = fileName,
            Reason = reason,
            FileAttributes = fileAttributes
        });
    }

    /// <summary>The same entry as a public synthetic record, for the test package's scripted watches and volumes.</summary>
    public static SyntheticJournalRecord CreateSynthetic(
        ulong recordNumber,
        long usn,
        string fileName,
        SyntheticJournalReason reason = SyntheticJournalReason.Close,
        FileAttributes fileAttributes = FileAttributes.Normal)
    {
        return new SyntheticJournalRecord
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = RootRecordNumber,
            UpdateSequenceNumber = usn,
            FileName = fileName,
            Reason = reason,
            FileAttributes = fileAttributes
        };
    }
}
