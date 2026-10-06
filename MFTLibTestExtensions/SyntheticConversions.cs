using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     Turns the public synthetic descriptions into the internal production values immediately before production
///     code runs. This is data translation only: filtering, path building and mutation stay in production.
/// </summary>
internal static class SyntheticConversions
{
    public static UsnJournalCursor ToProduction(this SyntheticJournalCursor cursor) =>
        new(cursor.JournalIdentifier, cursor.NextUpdateSequenceNumber);

    public static SyntheticJournalCursor ToSynthetic(this UsnJournalCursor cursor) =>
        new(cursor.JournalId, cursor.NextUsn);

    public static MftRecord ToProduction(this SyntheticScanRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = record.RecordNumber,
            ParentRecordNumber = record.ParentRecordNumber,
            InUse = record.InUse,
            IsDirectory = record.IsDirectory,
            SizeKnown = record.IsSizeKnown,
            FileName = record.FileName,
            FileAttributes = record.FileAttributes
                             ?? (record.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal),
            Size = record.Size,
            ModifiedFileTime = (record.LastWriteTime ?? DateTime.UnixEpoch).ToFileTimeUtc(),
            SequenceNumber = record.SequenceNumber
        });
    }

    public static UsnJournalEntry ToProduction(this SyntheticJournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record.RecordNumber,
            ParentRecordNumber = record.ParentRecordNumber,
            SequenceNumber = record.SequenceNumber,
            Usn = record.UpdateSequenceNumber,
            TimestampUtc = record.Timestamp ?? DateTime.UnixEpoch,
            Reason = (UsnReason)(uint)record.Reason,
            FileAttributes = record.FileAttributes,
            FileName = record.FileName
        });
    }

    public static UsnJournalEntry[] ToProduction(this IReadOnlyList<SyntheticJournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return [.. records.Select(record => record.ToProduction())];
    }
}
