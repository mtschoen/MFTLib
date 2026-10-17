using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

public abstract class BrokerBlockTestBase
{
    internal static readonly UsnJournalCursor ArmedCursor = new(71, 12345);
    internal static readonly UsnJournalCursor AdvancedCursor = new(71, 12500);
    internal static readonly NtfsVolumeInformation VolumeInformation = new(1024L * 100000, 1024);

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
        created.Value = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = scanDrive,
            QueryVolumeInformation = queryVolumeInfo ?? (_ => VolumeInformation),
            ReadJournal = (_, since, _) =>
            {
                change(created.Value!.Sections.Single().Block);
                return (Array.Empty<UsnJournalEntry>(), since);
            }
        }));
        return created.Value;
    }
}
