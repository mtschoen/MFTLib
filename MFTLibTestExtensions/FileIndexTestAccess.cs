using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Applies synthetic journal batches to an index in consumer tests.</summary>
public static class FileIndexTestAccess
{
    /// <summary>
    ///     Applies entries to the current block, advances its cursor and raises
    ///     <see cref="FileIndex.Changed" /> before returning the applied changes.
    /// </summary>
    /// <param name="index">The index receiving the synthetic batch.</param>
    /// <param name="driveLetter">The drive whose block receives the entries.</param>
    /// <param name="entries">The synthetic journal entries to apply.</param>
    /// <param name="journalId">The journal identifier stamped into the block.</param>
    /// <param name="nextUsn">The post-batch cursor stamped into the block.</param>
    /// <returns>The changes applied to the drive.</returns>
    public static IReadOnlyList<FileChange> ApplyJournalEntries(FileIndex index, char driveLetter,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index.ApplyJournalEntries(driveLetter, entries, journalId, nextUsn);
    }
}
