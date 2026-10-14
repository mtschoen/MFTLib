using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     The fake volumes an in-process broker serves. A null ScanDrive, WatchDrive or
///     GrowUsnJournal refuses that operation, as the real host does; a null ReadJournal answers
///     "nothing new" from every cursor.
/// </summary>
public sealed record ScriptedBrokerVolumes
{
    /// <summary>Arms a drive's journal cursor before its scan, and bounds a watch's backlog.</summary>
    public required Func<DriveKey, SyntheticJournalCursor> QueryJournalCursor { get; init; }

    /// <summary>
    ///     The records of one drive scan, in batches. The harness writes them through the production row
    ///     writer and filter. Null refuses every scan with "Broker has no scan source".
    /// </summary>
    public Func<ScriptedScan, IEnumerable<IReadOnlyList<SyntheticScanRecord>>>? ScanDrive { get; init; }

    /// <summary>Bounded catch-up after a scan. Null answers "nothing new" from every cursor.</summary>
    public Func<DriveKey, SyntheticJournalCursor, int, (SyntheticJournalRecord[] Entries, SyntheticJournalCursor Updated)>? ReadJournal
    {
        get;
        init;
    }

    /// <summary>Streams a drive's journal for a watch. Null refuses every watch.</summary>
    public Func<DriveKey, SyntheticJournalCursor, CancellationToken,
        IAsyncEnumerable<(SyntheticJournalRecord[] Entries, SyntheticJournalCursor Cursor)>>? WatchDrive
    { get; init; }

    /// <summary>Answers volume sizing queries. Null answers a small fixed volume (256 KiB of 1024-byte records).</summary>
    internal Func<DriveKey, NtfsVolumeInformation>? QueryVolume { get; init; }

    /// <summary>Grows a drive's journal to the requested settings. Null refuses every grow request.</summary>
    public Func<DriveKey, UsnJournalSettings, UsnJournalSettings>? GrowUsnJournal { get; init; }
}
