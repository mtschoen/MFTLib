using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>Builds <see cref="UsnJournalEntry" /> values for tests without reading a journal.</summary>
public static class SyntheticJournalEntry
{
    /// <summary>Creates one journal entry.</summary>
    /// <param name="options">The columns of the entry.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    public static UsnJournalEntry Create(SyntheticJournalEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = options.RecordNumber,
            ParentRecordNumber = options.ParentRecordNumber,
            SequenceNumber = options.SequenceNumber,
            Usn = options.Usn,
            Timestamp = options.TimestampUtc ?? DateTime.UnixEpoch,
            Reason = options.Reason,
            FileAttributes = options.FileAttributes,
            FileName = options.FileName
        });
    }
}
