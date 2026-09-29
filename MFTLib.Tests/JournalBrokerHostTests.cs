using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The host's scan, control and progress behavior over the wire, driven with raw frames through
// HostChannelHarness. The scan tests install JournalBrokerHost's process-wide progress throttle,
// so the class runs serially. Members here carry scan or control in their names; the watch
// partials keep their own.
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostTests
{
    static readonly UsnJournalCursor ScanArmedCursor = new(7UL, 100L);
    static readonly NtfsVolumeInformation ControlVolume = new(1024 * 1000, 1024, 512, 4096, 100, 10);

    static readonly string[] ScanKeepFileNamesGit = [".git"];
    static readonly string[] ScanKeepFileNamesGitUppercase = [".GIT"];
    static readonly string[] ScanKeepFileNamesNonMatching = ["other.txt"];

    static readonly MftRecord[] DirectoryIndexSampleRecords =
    [
        new(100, 5, new MftRecordFields(3, FileAttributes.Directory), "repo", null),
        new(101, 100, new MftRecordFields(1, FileAttributes.Archive), ".git", null),
        new(102, 100, new MftRecordFields(1, FileAttributes.Archive), "file.txt", null)
    ];

    static JournalBrokerHost ScanHost(
        UsnJournalCursorQuery? queryCursor = null,
        MftRecordBatchSource? scanDrive = null,
        UsnJournalCatchUpSource? readJournal = null,
        JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null,
        GrowUsnJournalQuery? growUsnJournal = null,
        int processorCount = 4,
        TimeProvider? timeProvider = null)
    {
        return new JournalBrokerHost(
            queryCursor ?? (_ => ScanArmedCursor),
            scanDrive ?? ((_, _, _, _, _) => [[ScanRecord(5, ".", 3)]]),
            readJournal ?? ((_, since) => (Array.Empty<UsnJournalEntry>(), since)),
            watchDrive,
            queryVolumeInfo,
            growUsnJournal,
            processorCount,
            timeProvider);
    }

    static MftRecord ScanRecord(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags, FileAttributes.Archive, 100), name, null);
    }

    static UsnJournalEntry ScanEntry()
    {
        return JournalEntryFactory.Create(100, 110, "a.txt", UsnReason.FileCreate | UsnReason.Close);
    }

    // Opens a scan channel on the drive and returns everything the host writes on it, in order,
    // until it closes the pipe.
    static async Task<List<BrokerFrame>> ScanFramesAsync(HostChannelHarness harness, char drive = 'C',
        string sectionName = "section", BrokerScanProfile profile = BrokerScanProfile.Full,
        IReadOnlyCollection<string>? keepFileNames = null)
    {
        var pipe = await harness.OpenScanChannelAsync(drive, sectionName, profile, keepFileNames);
        return await HostChannelHarness.ReadToEndAsync(pipe);
    }

    static int InUseRowCount(RecordingBlockSectionWriter writer)
    {
        return writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0);
    }
}
