namespace MFTLib;

/// <summary>
/// Tracks position in a volume's USN journal for resumable reads.
/// Persist this between runs to enable incremental scanning.
/// </summary>
public readonly struct UsnJournalCursor
{
    /// <summary>USN journal instance ID. Changes if the journal is deleted and recreated.</summary>
    public ulong JournalId { get; }

    /// <summary>Next USN to read from. Pass this to ReadUsnJournal to resume.</summary>
    public long NextUsn { get; }

    public UsnJournalCursor(ulong journalId, long nextUsn)
    {
        JournalId = journalId;
        NextUsn = nextUsn;
    }
}
