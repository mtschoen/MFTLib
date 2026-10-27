using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

sealed record BrokerHostOptions
{
    public Func<VolumeKey, UsnJournalCursor>? QueryCursor { get; init; }
    public MftRecordBatchSource? ScanDrive { get; init; }
    public Func<VolumeKey, UsnJournalCursor, int,
        (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? ReadJournal
    { get; init; }
    public Func<VolumeKey, UsnJournalCursor, ISourceOperationReporter, CancellationToken,
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>>? WatchDrive
    { get; init; }
    public Func<VolumeKey, NtfsVolumeInformation>? QueryVolumeInformation { get; init; }
    public Func<VolumeKey, long, long, UsnJournalSettings>? GrowUsnJournal { get; init; }
    public int? ProcessorCount { get; init; } = 4;
    public TimeProvider? TimeProvider { get; init; }
}

static class BrokerHostFactory
{
    public static JournalBrokerHost Create(BrokerHostOptions? options = null)
    {
        options ??= new BrokerHostOptions();
        return new JournalBrokerHost(new ScriptedVolumeAccess
        {
            CursorQuery = options.QueryCursor ?? (_ => new UsnJournalCursor(7, 1000)),
            RecordBatches = options.ScanDrive ?? ((_, _, _, _, _, _) =>
                [[Record(5, ".", 3)], [Record(20, "file.txt")]]),
            JournalRead = options.ReadJournal ?? ((_, since, _) => ([], since)),
            JournalWatch = options.WatchDrive ?? ScriptedVolumeAccess.UnsupportedWatch,
            VolumeInformation = options.QueryVolumeInformation ?? (_ => new NtfsVolumeInformation(1024 * 1000, 1024)),
            JournalGrow = options.GrowUsnJournal ?? ScriptedVolumeAccess.UnsupportedGrow
        }, options.ProcessorCount, options.TimeProvider);
    }

    public static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name);
    }

    public static NtfsVolumeInformation NoVolumeInformation(VolumeKey drive)
    {
        throw new NotSupportedException("Broker has no volume information source");
    }
}
