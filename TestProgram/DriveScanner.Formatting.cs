using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// One formatter per result type, shared by every mode that prints it, so each property of a
// record or journal entry is shown wherever that type appears.
partial class DriveScanner
{
    void PrintRecords(MftRecord[] records)
    {
        foreach (var record in records.Take(EntriesShown))
        {
            _writeLine(FormatRecord(record));
        }
    }

    void PrintEntries(UsnJournalEntry[] entries)
    {
        foreach (var entry in entries.Take(EntriesShown))
        {
            _writeLine(FormatEntry(entry));
        }
    }

    internal static string FormatRecord(MftRecord record)
    {
        var repeatsName = record.FullPath is null || record.FullPath == record.FileName;
        var name = repeatsName ? string.Empty : $" name {record.FileName}";
        var size = record.SizeKnown ? $"{record.Size}" : "unknown";
        var modified = record.ModifiedUtc == DateTime.MinValue ? "none" : record.ModifiedUtc.ToString("u");
        return $"  {record}{name} [record {record.RecordNumber} sequence {record.SequenceNumber} " +
               $"parent {record.ParentRecordNumber}] {(record.IsDirectory ? "directory" : "file")} " +
               $"{(record.InUse ? "in use" : "freed")} {record.FileAttributes} size {size} modified {modified}";
    }

    internal static string FormatEntry(UsnJournalEntry entry)
    {
        var flags = new[]
        {
            entry.IsCreate ? "create" : null,
            entry.IsDelete ? "delete" : null,
            entry.IsRename ? "rename" : null,
            entry.IsClose ? "close" : null
        }.OfType<string>();
        return $"  USN {entry.Usn} {entry.Timestamp:u} {entry} parent {entry.ParentRecordNumber} " +
               $"sequence {entry.SequenceNumber} {entry.FileAttributes} flags [{string.Join(" ", flags)}]";
    }

    internal static string FormatSettings(UsnJournalSettings settings)
    {
        return $"maximum size {settings.MaximumSize} bytes, allocation delta {settings.AllocationDelta} bytes";
    }

    internal static string FormatTimings(MftParseTimings timings)
    {
        return $"{timings.TotalRecords} records; native IO {timings.NativeIoMs:F1}ms, fixup " +
               $"{timings.NativeFixupMs:F1}ms, parse {timings.NativeParseMs:F1}ms, total " +
               $"{timings.NativeTotalMs:F1}ms; marshal {timings.MarshalMs:F1}ms";
    }
}
