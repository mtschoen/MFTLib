using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using TestProgram;

namespace MFTLib.Tests;

// The TestProgram modes behind a command line: argument errors, the unelevated scan-drive mode
// over an in-process broker, and the volume modes over faked journal and record reads.
[TestClass]
[DoNotParallelize]
public class DriveScannerModeTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"driveScannerModes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void Run_UnknownOption_PrintsUsageAndReturnsTwoWithoutTouchingElevation()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => throw new AssertFailedException("A bad command line must not reach elevation."),
            _writeLine = lines.Add
        };

        var result = scanner.Run(["usn-read", "--verbose"]);

        Assert.AreEqual(2, result);
        Assert.IsTrue(lines.Any(line => line.Contains("Unknown option --verbose")));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Usage:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Run_ScanDrive_NotElevated_ScansThroughTheBrokerWithoutSelfElevating()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();
        string? launchedFor = null;
        var scanner = ScannerOverBroker(broker, lines);
        scanner._createBlockPath = letter =>
        {
            launchedFor = letter.ToString();
            return Path.Combine(_directory, "block.mlix");
        };

        var result = scanner.Run(["scan-drive", "c:"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual("C", launchedFor);
        Assert.IsTrue(lines.Contains("Block holds 21 rows; 0 records skipped"));
        Assert.IsTrue(lines.Contains($"Armed cursor: journal {Armed.JournalId} at USN {Armed.NextUsn}"));
        Assert.IsTrue(lines.Contains("Catch-up held; advanced cursor at USN 1000"));
        Assert.IsTrue(lines.Contains("=== Drive c: done ==="));
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "block.mlix")), "The scan's block is deleted on close.");
    }

    [TestMethod]
    public async Task Run_ScanDrive_SeveralDrives_LaunchesOneBrokerForTheWholeRun()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();
        var launches = 0;
        var scanner = ScannerOverBroker(broker, lines);
        var process = broker.Process;
        scanner._launchBroker = _ =>
        {
            launches++;
            return Task.FromResult(process);
        };
        scanner._createBlockPath = letter => Path.Combine(_directory, $"block-{letter}.mlix");

        var result = scanner.Run(["scan-drive", "C", "D:"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Block holds ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("=== Drive D: done ==="));
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_CatchUpLostByTheJournal_ReportsTheLoss()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(7, 5000, 9000, 4096, 32768));
        await using var broker = new InProcessBroker(CreateHost(
            readJournal: (_, _, _) => throw new IOException("catch-up read failed")));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(broker, lines);

        await scanner.ScanDrivesThroughBrokerAsync(["C"], CancellationToken.None);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Block holds ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("Catch-up lost: CheckpointTrimmed"));
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_LaunchDeclined_PrintsTheError()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _launchBroker = _ => throw new InvalidOperationException("UAC prompt declined"),
            _readVolumeSerial = _ => 1,
            _createBlockPath = _ => Path.Combine(_directory, "never.mlix"),
            _writeLine = lines.Add
        };

        await scanner.ScanDrivesThroughBrokerAsync(["C"], CancellationToken.None);

        Assert.IsTrue(lines.Contains("Error launching the broker: UAC prompt declined"));
        Assert.IsFalse(lines.Any(line => line.Contains("done")));
    }

    [TestMethod]
    public void Run_ReadRecords_Elevated_PrintsTheRecordCountAndPaths()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._readAllRecords = _ =>
            [new MftRecord(5, 5, new MftRecordFields(3), "root", null), new MftRecord(20, 5, new MftRecordFields(1), "file.txt", null)];

        var result = scanner.Run(["read-records", "T"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Read 2 records (1 directories) in ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("  root"));
        Assert.IsTrue(lines.Contains("  file.txt"));
        Assert.IsTrue(lines.Contains("=== Drive T: done ==="));
    }

    [TestMethod]
    public void Run_UsnQuery_Elevated_PrintsTheCursorAndSizing()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._queryJournal = _ => Armed;
        scanner._queryJournalSettings = _ => new UsnJournalSettings { MaximumSize = 32768, AllocationDelta = 4096 };

        scanner.Run(["usn-query", "T"]);

        Assert.IsTrue(lines.Contains("Journal 7, next USN 1000"));
        Assert.IsTrue(lines.Contains("  maximum size 32768 bytes, allocation delta 4096 bytes"));
    }

    [TestMethod]
    public void Run_UsnRead_Elevated_ReadsTheJournalFromTheCursorArmedBeforeTheScan()
    {
        var lines = new List<string>();
        var order = new List<string>();
        UsnJournalCursor? readSince = null;
        var scanner = ElevatedScanner(lines);
        scanner._queryJournal = _ =>
        {
            order.Add("arm");
            return Armed;
        };
        scanner._readAllRecords = _ =>
        {
            order.Add("scan");
            return [new MftRecord(5, 5, new MftRecordFields(3), "root", null)];
        };
        scanner._readJournal = (_, since) =>
        {
            order.Add("catch-up");
            readSince = since;
            return ([JournalEntryFactory.Create(20, 1200, "file.txt")], new UsnJournalCursor(7, 1500));
        };

        scanner.Run(["usn-read", "T"]);

        CollectionAssert.AreEqual(new[] { "arm", "scan", "catch-up" }, order);
        Assert.AreEqual(Armed, readSince);
        Assert.IsTrue(lines.Contains("Catch-up read 1 entries, cursor now 1500"));
        Assert.IsTrue(lines.Any(line => line.Contains("file.txt")));
    }

    [TestMethod]
    public void Run_UsnWatch_Elevated_PrintsBatchesUntilTheWatchIsCancelled()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._queryJournal = _ => Armed;
        var cancellation = new CancellationTokenSource();
        TimeSpan? requestedDuration = null;
        UsnJournalCursor? watchedFrom = null;
        scanner._createWatchCancellation = duration =>
        {
            requestedDuration = duration;
            return cancellation;
        };
        scanner._watchJournal = (_, since, token) =>
        {
            watchedFrom = since;
            return OneBatchThenCancelled(cancellation, token);
        };

        scanner.Run(["usn-watch", "T", "--seconds", "3"]);

        Assert.AreEqual(TimeSpan.FromSeconds(3), requestedDuration);
        Assert.AreEqual(Armed, watchedFrom);
        Assert.IsTrue(lines.Contains("Batch of 1 entries, cursor now 1100"));
        Assert.IsTrue(lines.Contains("Watch ended."));
        Assert.IsTrue(lines.Contains("=== Drive T: done ==="));
    }

    [DataTestMethod]
    [DataRow("read-records")]
    [DataRow("usn-query")]
    [DataRow("usn-read")]
    [DataRow("usn-watch")]
    public void Run_VolumeMode_VolumeOpenFails_PrintsTheErrorAndCarriesOn(string mode)
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._openVolume = _ => throw new IOException("Access denied");

        var result = scanner.Run([mode, "T"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Error on drive T: Access denied"));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Completed at ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_VolumeMode_NotElevated_SelfElevatesWithTheOriginalArguments()
    {
        string? relaunchedWith = null;
        var elevationTimeout = 0;
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (arguments, timeoutMilliseconds) =>
            {
                relaunchedWith = arguments;
                elevationTimeout = timeoutMilliseconds;
                return true;
            },
            _writeLine = _ => { }
        };

        var result = scanner.Run(["usn-watch", "C", "--seconds", "5"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual("usn-watch C --seconds 5", relaunchedWith);
        Assert.AreEqual(60000 + 5000, elevationTimeout, "The elevation wait covers the requested watch time.");
    }

    static DriveScanner ElevatedScanner(List<string> lines)
    {
        return new DriveScanner
        {
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _openVolume = letter => MftVolume.Open(letter),
            _writeLine = lines.Add
        };
    }

    DriveScanner ScannerOverBroker(InProcessBroker broker, List<string> lines)
    {
        return new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => throw new AssertFailedException("scan-drive must not self-elevate."),
            _tryRunElevated = (_, _) => throw new AssertFailedException("scan-drive must not self-elevate."),
            _launchBroker = _ => Task.FromResult(broker.Process),
            _readVolumeSerial = _ => 4242,
            _createBlockPath = _ => Path.Combine(_directory, "block.mlix"),
            _writeLine = lines.Add
        };
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> OneBatchThenCancelled(
        CancellationTokenSource cancellation, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        yield return ([JournalEntryFactory.Create(30, 1100, "created.txt")], new UsnJournalCursor(7, 1100));
        await cancellation.CancelAsync();
        token.ThrowIfCancellationRequested();
    }

    static JournalBrokerHost CreateHost(UsnJournalCatchUpSource? readJournal = null)
    {
        return new JournalBrokerHost(
            _ => Armed,
            (_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]],
            readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
            queryVolumeInfo: _ => Volume,
            processorCount: 4);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }
}
