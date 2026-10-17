using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

public abstract class BrokerBlockTestBase
{
    internal static readonly UsnJournalCursor ArmedCursor = new(71, 12345);
    internal static readonly UsnJournalCursor AdvancedCursor = new(71, 12500);
    internal static readonly NtfsVolumeInformation VolumeInformation = new(1024L * 100000, 1024);

    internal static JournalBrokerHost CreateHost(
        Func<DriveKey, UsnJournalCursor>? queryCursor = null,
        MftRecordBatchSource? scanDrive = null,
        Func<DriveKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? readJournal = null,
        Func<DriveKey, NtfsVolumeInformation>? queryVolumeInfo = null,
        Func<DriveKey, long, long, UsnJournalSettings>? growUsnJournal = null)
    {
        return new JournalBrokerHost(
            new ScriptedVolumeAccess
            {
                CursorQuery = queryCursor ?? (_ => ArmedCursor),
                RecordBatches = scanDrive ?? ((_, _, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]),
                JournalRead = readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
                VolumeInformation = queryVolumeInfo ?? (_ => VolumeInformation),
                JournalGrow = growUsnJournal ?? ScriptedVolumeAccess.UnsupportedGrow
            },
            processorCount: 4);
    }

    internal static MftRecord Record(ulong recordNumber, string name, ushort flags = 1) =>
        new(recordNumber, 5, new MftRecordFields(flags), name);

    internal static BlockScanTarget Target(uint volumeSerial = 123) => TestBlockSections.Target(volumeSerial);

    /// <summary>The connect callback of a producer that always runs on <paramref name="process" />.</summary>
    internal static Func<CancellationToken, Task<BrokerProcess>> Connect(BrokerProcess process) =>
        _ => Task.FromResult(process);

    internal static MftBlockProduceRequest Request(BlockScanTarget target) => new()
    {
        Drive = 'C',
        VolumeSerial = target.VolumeSerial,
        BlockPath = target.Path,
        DeleteOnClose = true
    };

    internal static Task<MftBlockProduceResult> ProduceAsync(BrokerProcess process, MftBlockProduceRequest request,
        MftScanOptions? options = null) =>
        new BrokerMftBlockProducer(Connect(process), options).CreateIndexSource().Producer(request,
            CancellationToken.None);

    /// <summary>
    ///     A broker whose host hands <paramref name="change" /> the drive's block once the host has
    ///     written it and sent ScanReady, before the client validates it.
    /// </summary>
    internal static InProcessBroker CreateBrokerChangingBlock(Action<BlockFile> change,
        MftRecordBatchSource? scanDrive = null, Func<DriveKey, NtfsVolumeInformation>? queryVolumeInfo = null)
    {
        var created = new StrongBox<InProcessBroker?>();
        created.Value = new InProcessBroker(CreateHost(scanDrive: scanDrive, queryVolumeInfo: queryVolumeInfo,
            readJournal: (_, since, _) =>
            {
                change(created.Value!.Sections.Single().Block);
                return (Array.Empty<UsnJournalEntry>(), since);
            }));
        return created.Value;
    }
}
