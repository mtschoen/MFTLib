using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Control, progress and watch behavior over HostChannelHarness. Journal overrides and
// diagnostics in the other partials mutate process-wide state, so this class is serial.
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostTests
{
    static readonly UsnJournalCursor ScanArmedCursor = new(7UL, 100L);
    static readonly NtfsVolumeInformation ControlVolume = new(1024 * 1000, 1024);

    static JournalBrokerHost ScanHost(
        Func<VolumeKey, UsnJournalCursor>? queryCursor = null,
        MftRecordBatchSource? scanDrive = null,
        Func<VolumeKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? readJournal = null,
        Func<VolumeKey, UsnJournalCursor, ISourceOperationReporter, CancellationToken, IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>>? watchDrive = null,
        Func<VolumeKey, NtfsVolumeInformation>? queryVolumeInfo = null,
        Func<VolumeKey, long, long, UsnJournalSettings>? growUsnJournal = null,
        int processorCount = 4,
        TimeProvider? timeProvider = null) =>
        BrokerHostFactory.Create(new()
        {
            QueryCursor = queryCursor ?? (_ => ScanArmedCursor),
            ScanDrive = scanDrive ?? ((_, _, _, _, _, _) => [[ScanRecord(5, ".", 3)]]),
            ReadJournal = readJournal,
            WatchDrive = watchDrive,
            QueryVolumeInformation = queryVolumeInfo ?? BrokerHostFactory.NoVolumeInformation,
            GrowUsnJournal = growUsnJournal,
            ProcessorCount = processorCount,
            TimeProvider = timeProvider
        });

    static MftRecord ScanRecord(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags, FileAttributes.Archive, 100), name);
    }

    // Opens a scan channel on the drive and returns everything the host writes on it, in order,
    // until it closes the pipe.
    static async Task<List<BrokerFrame>> ScanFramesAsync(HostChannelHarness harness, char drive = 'C',
        string? sectionName = null, IReadOnlyCollection<string>? retainedFileNames = null)
    {
        var pipe = await harness.OpenScanChannelAsync(drive, sectionName, retainedFileNames);
        return await HostChannelHarness.ReadToEndAsync(pipe);
    }

    // A control request answered after a channel failed proves the session outlived it.
    internal static async Task AssertControlStillAnswersAsync(HostChannelHarness harness)
    {
        var requestId = harness.NextRequestId();
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, requestId, 'C'));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, reply.Kind);
        Assert.AreEqual(requestId, reply.RequestId);
    }
}
