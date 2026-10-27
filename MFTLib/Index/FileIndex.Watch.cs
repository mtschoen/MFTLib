namespace MFTLib.Index;

public sealed partial class FileIndex
{
    // No caller token: a direct apply has no stop of its own, and the index's disposal token would
    // let disposal turn a batch already queued for the gate into a cancellation instead of the
    // ObjectDisposedException a disposed index reports.
    internal async Task<IReadOnlyList<FileChange>> ApplyJournalEntriesAsync(VolumeKey drive,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entries);
        var runtime = GetDriveRuntime(drive);
        var changes = await runtime.ApplyJournalEntriesCoreAsync(null, entries, journalIdentifier, nextUsn,
            CancellationToken.None).ConfigureAwait(false);
        runtime.RaiseChanged(changes);
        return changes;
    }
}
