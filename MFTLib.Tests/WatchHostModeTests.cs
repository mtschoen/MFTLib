using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// The SampleProgram.Watch scan-drive mode behind a command line: argument errors, the unelevated scan (a FileIndex over an
// in-process broker) and the self-elevation relaunch.
[TestClass]
[DoNotParallelize]
public class WatchHostModeTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"watchHostModes-{Guid.NewGuid():N}");
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
        var scanner = new SampleHost
        {
            _isElevated = () => throw new AssertFailedException("A bad command line must not reach elevation."),
            _writeLine = lines.Add
        };

        var result = scanner.Run(["scan-drive", "--verbose"]);

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
        var handle = broker.Handle;
        scanner._createBrokerSession = () => BrokerTestHarness.CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(handle);
        });

        var result = scanner.Run(["scan-drive", "C", "D:"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Index holds ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("=== Drive D: done ==="));
        Assert.IsTrue(handle.Ended.IsCompleted,
            "The scanner must dispose its session before returning.");
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_CatchUpLostByTheJournal_ReportsTheLoss()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(7, 5000, 9000, 4096, 32768));
        await using var broker = new InProcessBroker(CreateHost(
            readJournal: (_, _, _) => throw new IOException("catch-up read failed")));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(broker, lines);

        await scanner.RunThroughBrokerAsync(new WatchArguments(ProgramMode.ScanDrive, ["C"]), CancellationToken.None);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Index holds ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("Catch-up lost: CheckpointTrimmed"));
    }

    [TestMethod]
    public async Task ScanDriveThroughBroker_ScanFailsOnTheBroker_PrintsTheProducerFailure()
    {
        await using var broker = new InProcessBroker(CreateHost(
            scanDrive: (_, _, _, _, _, _) => throw new IOException("volume unreadable")));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(broker, lines);

        await scanner.RunThroughBrokerAsync(new WatchArguments(ProgramMode.ScanDrive, ["C"]), CancellationToken.None);

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
        var launches = 0;
        var handle = broker.Handle;
        scanner._createBrokerSession = () => BrokerTestHarness.CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(handle);
        });
        scanner._resolveDrive = _ => new IndexedDrive('Q', Path.Combine(_directory, "missing-root"), 4242);

        await scanner.RunThroughBrokerAsync(new WatchArguments(ProgramMode.ScanDrive, ["Q"]), CancellationToken.None);

        Assert.AreEqual(0, launches, "An offline drive needs no elevated broker.");
        Assert.IsTrue(lines.Contains("Error on drive Q: The drive is offline; nothing was scanned."),
            string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(line => line.StartsWith("Catch-up", StringComparison.Ordinal) ||
                                        line.Contains("done")));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ScanDriveThroughBroker_LaunchDeclined_PrintsTheError(bool failAtCreation)
    {
        var lines = new List<string>();
        var scanner = new SampleHost
        {
            _createBrokerSession = () => failAtCreation
                ? throw new InvalidOperationException("UAC prompt declined")
                : BrokerTestHarness.CreateSession(_ =>
                    throw new InvalidOperationException("UAC prompt declined")),
            _resolveDrive = ResolveDrive,
            _cacheDirectory = _directory,
            _writeLine = lines.Add
        };

        await scanner.RunThroughBrokerAsync(new WatchArguments(ProgramMode.ScanDrive, ["C"]), CancellationToken.None);

        var prefix = failAtCreation ? "Error creating broker session: " : "Error on drive C: ";
        Assert.IsTrue(lines.Any(line => line.StartsWith(prefix, StringComparison.Ordinal) &&
                                        line.Contains("UAC prompt declined", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(line => line.Contains("done", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_NotElevated_WhenTheRunRequiresElevation_SelfElevatesWithTheOriginalArguments()
    {
        string[] arguments = ["scan-drive", "C", "D"];
        IReadOnlyList<string>? relaunchedWith = null;
        var elevationTimeout = TimeSpan.Zero;
        var scanner = new SampleHost
        {
            _elevationNeed = _ => ElevationNeed.SelfElevate,
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
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);

        var result = scanner.Run(arguments);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(arguments, relaunchedWith!.ToArray());
        Assert.AreEqual(ElevationUtilities.DefaultElevatedTimeout, elevationTimeout);
    }

    // The library quotes each element for the child's command line, so the scanner hands over the
    // arguments exactly as it accepted them, whatever whitespace or quotes they hold.
    [DataTestMethod]
    [DataRow(new[] { "C\" --maximum-size 9000 --allocation-delta 4096" },
        DisplayName = "an option-injection positional stays one argument")]
    [DataRow(new[] { "scan-drive", "a\"b" }, DisplayName = "a literal quote survives")]
    [DataRow(new[] { "scan-drive", "a\\\"b" }, DisplayName = "a backslash before a quote survives")]
    [DataRow(new[] { "scan-drive", "C", "" }, DisplayName = "an empty value last survives")]
    [DataRow(new[] { "scan-drive", "", "C" }, DisplayName = "an empty value before another drive survives")]
    [DataRow(new[] { "scan-drive", "a\tb.txt" }, DisplayName = "a tab inside a value survives")]
    [DataRow(new[] { "scan-drive", "C:\\spaced directory\\" },
        DisplayName = "a trailing backslash last survives")]
    [DataRow(new[] { "scan-drive", "C:\\spaced directory\\", "D" },
        DisplayName = "a trailing backslash before another drive survives")]
    public void Run_NotElevated_RelaunchesWithEachArgumentVerbatim(string[] arguments)
    {
        IReadOnlyList<string>? relaunchedWith = null;
        var scanner = new SampleHost
        {
            _elevationNeed = _ => ElevationNeed.SelfElevate,
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (relaunchArguments, _) =>
            {
                relaunchedWith = relaunchArguments;
                return true;
            },
            _writeLine = _ => { }
        };
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);

        Assert.AreEqual(0, scanner.Run(arguments));

        CollectionAssert.AreEqual(arguments, relaunchedWith!.ToArray(),
            "The child must receive exactly the arguments the parent accepted.");
    }

    SampleHost ScannerOverBroker(InProcessBroker broker, List<string> lines)
    {
        var scanner = new SampleHost
        {
            _isElevated = () => false,
            _getEnvironmentVariable = _ => null,
            _canSelfElevate = () => throw new AssertFailedException("scan-drive must not self-elevate."),
            _tryRunElevated = (_, _) => throw new AssertFailedException("scan-drive must not self-elevate."),
            _createBrokerSession = () => BrokerTestHarness.CreateSession(_ => Task.FromResult(broker.Handle)),
            _resolveDrive = ResolveDrive,
            _cacheDirectory = _directory,
            _writeLine = lines.Add
        };
        // The run is attended and unelevated, so it shows the heads-up dialog before the broker launch.
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);
        return scanner;
    }

    IndexedDrive ResolveDrive(string letter)
    {
        var driveLetter = char.ToUpperInvariant(letter[0]);
        return new IndexedDrive(driveLetter, _directory, 4242);
    }

    static JournalBrokerHost CreateHost(UsnJournalCatchUpSource? readJournal = null, MftRecordBatchSource? scanDrive = null)
    {
        return new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                _ => Armed,
                scanDrive ?? ((_, _, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]),
                readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
                QueryVolumeInformation: _ => Volume),
            processorCount: 4);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name);
    }
}
