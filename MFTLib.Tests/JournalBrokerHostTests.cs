using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Scan, control and watch behavior over HostChannelHarness. Journal overrides and
// diagnostics in the other partials mutate process-wide state, so this class is serial.
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostTests
{
    static readonly UsnJournalCursor ScanArmedCursor = new(7UL, 100L);
    static readonly NtfsVolumeInformation ControlVolume = new(1024 * 1000, 1024);

    static readonly MftRecord[] RetentionSampleRecords =
    [
        new(100, 5, new MftRecordFields(3, FileAttributes.Directory), "repo"),
        new(101, 100, new MftRecordFields(1, FileAttributes.Archive), ".git"),
        new(102, 100, new MftRecordFields(1, FileAttributes.Archive), "file.txt")
    ];

    static JournalBrokerHost ScanHost(
        Func<DriveKey, UsnJournalCursor>? queryCursor = null,
        MftRecordBatchSource? scanDrive = null,
        Func<DriveKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? readJournal = null,
        Func<DriveKey, UsnJournalCursor, ISourceOperationReporter, CancellationToken, IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>>? watchDrive = null,
        Func<DriveKey, NtfsVolumeInformation>? queryVolumeInfo = null,
        Func<DriveKey, long, long, UsnJournalSettings>? growUsnJournal = null,
        int processorCount = 4,
        TimeProvider? timeProvider = null)
    {
        return new JournalBrokerHost(
            new ScriptedVolumeAccess
            {
                CursorQuery = queryCursor ?? (_ => ScanArmedCursor),
                RecordBatches = scanDrive ?? ((_, _, _, _, _, _) => [[ScanRecord(5, ".", 3)]]),
                JournalRead = readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
                JournalWatch = watchDrive ?? ScriptedVolumeAccess.UnsupportedWatch,
                VolumeInformation = queryVolumeInfo ??
                    (_ => throw new NotSupportedException("Broker has no volume information source")),
                JournalGrow = growUsnJournal ?? ScriptedVolumeAccess.UnsupportedGrow
            },
            processorCount,
            timeProvider);
    }

    static MftRecord ScanRecord(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags, FileAttributes.Archive, 100), name);
    }

    static UsnJournalEntry ScanEntry()
    {
        return JournalEntries.Create(100, 110, "a.txt", UsnReason.FileCreate | UsnReason.Close);
    }

    // Opens a scan channel on the drive and returns everything the host writes on it, in order,
    // until it closes the pipe.
    static async Task<List<BrokerFrame>> ScanFramesAsync(HostChannelHarness harness, char drive = 'C',
        string sectionName = "section", IReadOnlyCollection<string>? retainedFileNames = null)
    {
        var pipe = await harness.OpenScanChannelAsync(drive, sectionName, retainedFileNames);
        return await HostChannelHarness.ReadToEndAsync(pipe);
    }

    static int InUseRowCount(RecordingBlockSectionWriter writer)
    {
        return writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0);
    }
}
