using MFTLib.Index;

namespace TestProgram;

// One formatter per Index result type, so every public field of a type is printed wherever it appears.
// Enum values print by name.
partial class DriveScanner
{
    /// <summary>A nullable number in invariant culture, or the words for its absence.</summary>
    internal static string Describe<T>(T? value, string absent) where T : struct, IFormattable
    {
        return value is { } present ? present.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : absent;
    }

    internal static string FormatKey(IndexRecordKey key)
    {
        return $"{key.DriveLetter}:{key.RecordNumber}:{key.ProducerKind}";
    }

    /// <summary>Every property of an entry, or the reason none can be read.</summary>
    internal static string FormatFileEntry(FileEntry entry)
    {
        if (!entry.IsValid)
        {
            return "(default entry: it references no snapshot)";
        }

        if (entry.IsDisposed)
        {
            return "(entry released: its index was disposed, so no property can be read)";
        }

        var parent = entry.Parent is { } parentEntry ? parentEntry.Name : "none";
        var size = entry.SizeKnown ? entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        return $"{entry.Path} [name {entry.Name}; key {FormatKey(entry.Id)}; {(entry.IsDirectory ? "directory" : "file")}; " +
               $"attributes {entry.Attributes}; size {size}; modified {entry.Modified:u}; parent {parent}; " +
               $"deleted {entry.IsDeleted}; valid {entry.IsValid}; disposed {entry.IsDisposed}]";
    }

    void PrintFileEntries(IEnumerable<FileEntry> entries, int limit)
    {
        var shown = 0;
        var total = 0;
        foreach (var entry in entries)
        {
            total++;
            if (shown < limit)
            {
                shown++;
                _writeLine($"  {FormatFileEntry(entry)}");
            }
        }

        _writeLine($"{total} entries; {shown} shown (limit {limit}).");
    }

    internal static string FormatCheckpointLoss(JournalCheckpointLoss loss)
    {
        return $"drive {loss.DriveLetter}: cause {loss.Cause}; detected during {loss.DetectedDuring}; maximum size " +
               $"{loss.MaximumSize}; allocation delta {loss.AllocationDelta}; bytes behind " +
               $"{Describe(loss.BytesBehind, "unknown")}; size that would have retained it " +
               $"{Describe(loss.SizeThatWouldHaveRetained, "unknown")}";
    }

    /// <summary>Says what a lost catch-up means and what the consumer does next, including the recovery limit.</summary>
    internal static string ExplainCatchUpLoss(JournalCatchUpLostException exception)
    {
        return exception.RecoveryStopped
            ? $"Recovery stopped after {FileIndex.LostCatchUpRecoveryLimit} consecutive lost catch-ups: the last block " +
              "stays queryable, the watch is refused, and only a rescan restarts it."
            : $"The catch-up was lost and recovery continues: the index rescans itself, up to " +
              $"{FileIndex.LostCatchUpRecoveryLimit} times in a row.";
    }

    internal static string FormatFileChange(FileChange change)
    {
        var previous = change.PreviousPath is null ? string.Empty : $"; previous path {change.PreviousPath}";
        return $"change {change.Kind} at {change.Timestamp:u}: {change.Path}{previous}; entry {FormatFileEntry(change.Entry)}";
    }

    internal static string FormatWatchState(DriveWatchState state)
    {
        return $"watch state drive {state.DriveLetter}: {state.State}; version {state.Version}; fault " +
               $"{(state.Fault is { } fault ? FormatWatchFault(fault) : "none")}";
    }

    internal static string FormatWatchFault(WatchFault fault)
    {
        var detail = $"{fault.Kind} on drive {fault.DriveLetter}: {fault.Exception.GetType().Name}: {fault.Exception.Message}";
        return fault.Exception is JournalCatchUpLostException lost ? $"{detail} ({ExplainCatchUpLoss(lost)})" : detail;
    }

    internal static string FormatOperationResult(DriveOperationResult result)
    {
        var failure = result.Failure is { } exception ? $"; failure {exception.GetType().Name}: {exception.Message}" : string.Empty;
        return $"drive {result.DriveLetter}: {result.Outcome}{failure}";
    }
}
