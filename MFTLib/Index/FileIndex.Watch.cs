namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal IReadOnlyList<FileChange> ApplyJournalEntries(DriveKey drive,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entries);
        var runtime = GetDriveRuntime(drive);
        var changes = runtime.ApplyJournalEntriesCore(null, entries, journalIdentifier, nextUsn);
        runtime.RaiseChanged(changes);
        return changes;
    }
}
