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
        return SyntheticJournalEntry.Create(new SyntheticJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = RootRecordNumber,
            Usn = usn,
            FileName = fileName,
            Reason = reason,
            FileAttributes = fileAttributes
        });
    }
}
