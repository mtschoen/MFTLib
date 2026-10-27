using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// The SampleProgram.Watch scan verb behind a command line: argument errors, the unelevated scan (a FileIndex over an
// in-process broker) and the self-elevation relaunch.
[TestClass]
[DoNotParallelize]
public sealed class WatchHostModeTests : IDisposable
{
    TempDirectory _directoryOwner = null!;
    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("watchHostModes");
        _directory = _directoryOwner.Path;
        FileUtilities.Hooks._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        _directoryOwner.Dispose();
    }

    public void Dispose() => Cleanup();

    [TestMethod]
    public void Run_UnknownOption_PrintsUsageAndReturnsTwoWithoutTouchingElevation()
    {
        var lines = new List<string>();
        var scanner = new SampleHost(new LineCollectingWriter(lines), TextReader.Null)
        {
            _isElevated = () => throw new AssertFailedException("A bad command line must not reach elevation.")
        };

        var result = scanner.Run(["scan", "--verbose"]);

        Assert.AreEqual(2, result);
        Assert.Contains(line => line.Contains("Unknown option --verbose"), lines);
        Assert.Contains(line => line.StartsWith("Usage:", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public async Task Run_Scan_NotElevated_ScansThroughTheBrokerWithoutSelfElevating()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 10, 16, 12, 34, 56, TimeSpan.Zero)));
        var sessionLaunch = Task.FromResult(broker.Handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, ["C"]), session, ResolveDrive, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.Contains("Index holds 2 rows; 0 records skipped", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("Catch-up held; watch supported: True", lines);
        Assert.Contains(line => line.StartsWith("  C: Finished: ", StringComparison.Ordinal), lines);
        Assert.Contains("=== Drive C: done ===", lines);
    }

    [TestMethod]
    public async Task Run_Scan_SeveralDrives_LaunchesOneBrokerForTheWholeRun()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var handle = broker.Handle;
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, ["C", "D"]), session, ResolveDrive, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Index holds ", StringComparison.Ordinal)));
        Assert.Contains("=== Drive D: done ===", lines);
        Assert.IsTrue(handle.Ended.IsCompleted,
            "The scanner must dispose its session before returning.");
    }

    [TestMethod]
    public async Task ScanThroughBroker_CatchUpLostByTheJournal_ReportsTheLoss()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 5000, 9000, 4096, 32768));
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadJournal = (_, _, _) => throw new IOException("catch-up read failed")
        }));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var sessionLaunch = Task.FromResult(broker.Handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, ["C"]), session, ResolveDrive, CancellationToken.None);

        Assert.Contains(line => line.StartsWith("Index holds ", StringComparison.Ordinal), lines);
        Assert.Contains("Catch-up lost: CheckpointTrimmed", lines);
    }

    [TestMethod]
    public async Task ScanThroughBroker_ScanFailsOnTheBroker_PrintsTheProducerFailure()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => throw new IOException("volume unreadable")
        }));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var sessionLaunch = Task.FromResult(broker.Handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, ["C"]), session, ResolveDrive, CancellationToken.None);

        Assert.Contains(line => line.StartsWith("Error on drive C: ", StringComparison.Ordinal) &&
                                        line.Contains("volume unreadable"), lines, string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.Contains("done"), lines);
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
    public async Task ScanThroughBroker_DriveRootMissing_ReportsOfflineWithoutSuccessLines()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var handle = broker.Handle;
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, ["Q"]), session, _ => SyntheticIndexedDrive.Create(new VolumeKey('Q'), Path.Combine(_directory, "missing-root"), 4242), CancellationToken.None);

        Assert.Contains("Error on drive Q: The drive is offline; nothing was scanned.", lines,
            string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.StartsWith("Catch-up", StringComparison.Ordinal) ||
                                        line.Contains("done"), lines);
    }

    [TestMethod]
    public async Task ScanThroughBroker_LaunchDeclined_PrintsTheError()
    {
        var lines = new List<string>();
        var scanner = new SampleHost(new LineCollectingWriter(lines), TextReader.Null);
        var session = BrokerTestHarness.CreateSession(_ => throw new InvalidOperationException("UAC prompt declined"));

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, ["C"]), session, ResolveDrive, CancellationToken.None);

        Assert.Contains(line => line.StartsWith("Error on drive C: ", StringComparison.Ordinal) &&
                                        line.Contains("UAC prompt declined", StringComparison.Ordinal), lines,
            string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.Contains("done", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Run_BrokerSessionCannotBeCreated_ReturnsOneAndNeverReportsCompletion()
    {
        var lines = new List<string>();
        var scanner = new SampleHost(new LineCollectingWriter(lines), TextReader.Null)
        {
            _isElevated = () => true
        };

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.StartsWith("Error creating broker session: ", StringComparison.Ordinal), lines);
        Assert.DoesNotContain(line => line.StartsWith("Completed at", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
    }

    static SampleHost ScannerOverBroker(List<string> lines, TimeProvider? timeProvider = null)
    {
        var scanner = new SampleHost(new LineCollectingWriter(lines), TextReader.Null, timeProvider ?? new Microsoft.Extensions.Time.Testing.FakeTimeProvider())
        {
            _isElevated = () => false,
            _canSelfElevate = () => throw new AssertFailedException("scan must not self-elevate.")
        };
        // The run is attended and unelevated, so it shows the heads-up dialog before the broker launch.
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);
        return scanner;
    }

    IndexedRoot ResolveDrive(string letter)
    {
        var driveLetter = char.ToUpperInvariant(letter[0]);
        return SyntheticIndexedDrive.Create(new VolumeKey(driveLetter), _directory, 4242);
    }
}
