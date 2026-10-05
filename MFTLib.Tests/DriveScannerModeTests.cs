using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using TestProgram;

namespace MFTLib.Tests;

// The TestProgram modes behind a command line: argument errors, the unelevated scan-drive mode
// (a FileIndex over an in-process broker), and the volume modes over faked journal and record reads.
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
        var scanner = ScannerOverBroker(broker, lines);

        var result = scanner.Run(["scan-drive", "c:"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Index holds 2 rows; 0 records skipped"), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("Catch-up held; watch supported: True"));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  Finished: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("=== Drive c: done ==="));
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

        var result = scanner.Run(["scan-drive", "C", "D:"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Index holds ", StringComparison.Ordinal)));
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

        Assert.IsTrue(lines.Any(line => line.StartsWith("Index holds ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("Catch-up lost: CheckpointTrimmed"));
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_ScanFailsOnTheBroker_PrintsTheProducerFailure()
    {
        await using var broker = new InProcessBroker(CreateHost(
            scanDrive: (_, _, _, _, _) => throw new IOException("volume unreadable")));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(broker, lines);

        await scanner.ScanDrivesThroughBrokerAsync(["C"], CancellationToken.None);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Error on drive C: ", StringComparison.Ordinal) &&
                                        line.Contains("volume unreadable")), string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(line => line.Contains("done")));
    }

    [TestMethod]
    public void ResolveDrive_SyntheticDrives_RootAtTheFixtureDirectoryNotARealVolume()
    {
        foreach (var letter in new[] { "C", "D", "Z" })
        {
            var drive = ResolveDrive(letter);

            Assert.AreEqual(_directory, drive.RootDirectory);
            Assert.IsTrue(Directory.Exists(drive.RootDirectory));
        }
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_DriveRootMissing_ReportsOfflineWithoutSuccessLines()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(broker, lines);
        scanner._resolveDrive = _ => new IndexedDrive('Q', Path.Combine(_directory, "missing-root"), 4242);

        await scanner.ScanDrivesThroughBrokerAsync(["Q"], CancellationToken.None);

        Assert.IsTrue(lines.Contains("Error on drive Q: The drive is offline; nothing was scanned."),
            string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(line => line.StartsWith("Catch-up", StringComparison.Ordinal) ||
                                        line.Contains("done")));
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_LaunchDeclined_PrintsTheError()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _launchBroker = _ => throw new InvalidOperationException("UAC prompt declined"),
            _resolveDrive = ResolveDrive,
            _cacheDirectory = _directory,
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
        scanner._readAllRecords = (_, _, _) =>
            ([new MftRecord(5, 5, new MftRecordFields(3), "root", null), new MftRecord(20, 5, new MftRecordFields(1), "file.txt", null)], null);

        var result = scanner.Run(["read-records", "T"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Read 2 records (1 directories) in ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  root [record 5 ", StringComparison.Ordinal) &&
                                        line.Contains("directory in use")));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  file.txt [record 20 ", StringComparison.Ordinal) &&
                                        line.Contains("file in use")));
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
        scanner._readAllRecords = (_, _, _) =>
        {
            order.Add("scan");
            return ([new MftRecord(5, 5, new MftRecordFields(3), "root", null)], null);
        };
        scanner._readJournal = (_, since) =>
        {
            order.Add("catch-up");
            readSince = since;
            return ([JournalEntries.Create(20, 1200, "file.txt")], new UsnJournalCursor(7, 1500));
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
        scanner._createTimedCancellation = duration =>
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

    [DataTestMethod]
    [DataRow(new[] { "usn-watch", "C", "--seconds", "5" }, 60000 + 5000,
        DisplayName = "the elevation wait covers the requested watch time")]
    [DataRow(new[] { "stream-records", "C", "D", "--timeout-seconds", "120" }, 60000 + 240_000,
        DisplayName = "the elevation wait covers every drive's requested streaming timeout")]
    [DataRow(new[] { "stream-records", "C" }, -1,
        DisplayName = "streaming without a requested timeout waits without a limit (Timeout.Infinite)")]
    public void Run_VolumeMode_NotElevated_SelfElevatesWithTheOriginalArguments(string[] arguments, int expectedTimeoutMilliseconds)
    {
        IReadOnlyList<string>? relaunchedWith = null;
        var elevationTimeout = TimeSpan.Zero;
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (relaunchArguments, timeout) =>
            {
                relaunchedWith = relaunchArguments;
                elevationTimeout = timeout;
                return true;
            },
            _writeLine = _ => { }
        };

        var result = scanner.Run(arguments);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(arguments, relaunchedWith!.ToArray());
        Assert.AreEqual(TimeSpan.FromMilliseconds(expectedTimeoutMilliseconds), elevationTimeout,
            "The elevation wait covers the requested duration.");
    }

    // The library quotes each element for the child's command line, so the scanner hands over the
    // arguments exactly as it accepted them, whatever whitespace or quotes they hold.
    [DataTestMethod]
    [DataRow(new[] { "usn-grow", "--maximum-size", "8000", "--allocation-delta", "2048", "C\" --maximum-size 9000 --allocation-delta 4096" },
        DisplayName = "an option-injection positional stays one argument")]
    [DataRow(new[] { "find-name", "C", "--name", "a\"b" }, DisplayName = "a literal quote survives")]
    [DataRow(new[] { "find-name", "C", "--name", "a\\\"b" }, DisplayName = "a backslash before a quote survives")]
    [DataRow(new[] { "find-name", "C", "--name", "" }, DisplayName = "an empty value last survives")]
    [DataRow(new[] { "find-name", "C", "--name", "", "--include-freed" },
        DisplayName = "an empty value before another option survives")]
    [DataRow(new[] { "find-name", "C", "--name", "a\tb.txt" }, DisplayName = "a tab inside a value survives")]
    [DataRow(new[] { "find-name", "C", "--name", "C:\\spaced directory\\" },
        DisplayName = "a trailing backslash last survives")]
    [DataRow(new[] { "find-name", "C", "--name", "C:\\spaced directory\\", "--include-freed" },
        DisplayName = "a trailing backslash before another option survives")]
    public void Run_NotElevated_RelaunchesWithEachArgumentVerbatim(string[] arguments)
    {
        IReadOnlyList<string>? relaunchedWith = null;
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (relaunchArguments, _) =>
            {
                relaunchedWith = relaunchArguments;
                return true;
            },
            _writeLine = _ => { }
        };

        Assert.AreEqual(0, scanner.Run(arguments));

        CollectionAssert.AreEqual(arguments, relaunchedWith!.ToArray(),
            "The child must receive exactly the arguments the parent accepted.");
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
            _resolveDrive = ResolveDrive,
            _cacheDirectory = _directory,
            _writeLine = lines.Add
        };
    }

    IndexedDrive ResolveDrive(string letter)
    {
        var driveLetter = char.ToUpperInvariant(letter[0]);
        return new IndexedDrive(driveLetter, _directory, 4242);
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> OneBatchThenCancelled(
        CancellationTokenSource cancellation, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        yield return ([JournalEntries.Create(30, 1100, "created.txt")], new UsnJournalCursor(7, 1100));
        await cancellation.CancelAsync();
        token.ThrowIfCancellationRequested();
    }

    static JournalBrokerHost CreateHost(UsnJournalCatchUpSource? readJournal = null, MftRecordBatchSource? scanDrive = null)
    {
        return new JournalBrokerHost(
            _ => Armed,
            scanDrive ?? ((_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]),
            readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
            queryVolumeInfo: _ => Volume,
            processorCount: 4);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }
}
