using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using SampleProgram.Watch;
using System.Runtime.CompilerServices;

namespace MFTLib.Tests;

// The SampleProgram.Watch scan verb behind a command line: argument errors, the unelevated scan (a FileIndex over an
// in-process broker) and the self-elevation relaunch.
[TestClass]
[DoNotParallelize]
public sealed class WatchHostModeTests : IDisposable
{
    [TestMethod]
    [DataRow("add-root {source}\n\n", "Added root {source}: producer Mft")]
    [DataRow("frobnicate\n\n", "Unknown command 'frobnicate'. Type add-root <path>, or press Enter to stop.")]
    [DataRow("add-root relative/path\n\n", "Root 'relative/path' is neither a drive nor a full path.")]
    [DataRow("", "Watching T. Type add-root <path> to watch another root, or press Enter to stop.")]
    public async Task Watch_InteractiveCommands_KeepOneScanAndStopCleanly(string inputTemplate, string expectedTemplate)
    {
        using var attended = new UnattendedEnvironmentScope(null);
        var source = Path.Combine(_directory, "src");
        Directory.CreateDirectory(source);
        var scans = 0;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new BrokerHostOptions
        {
            ScanVolume = (_, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref scans);
                return [[BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(30, "src", 3),
                    new MftRecord(31, 30, new MftRecordFields(1), "a.txt")]];
            },
            WatchVolume = (_, since, _, token) => QuietWatch(since, token)
        }));
        var lines = new List<string>();
        using var input = new StringReader(inputTemplate.Replace("{source}", source, StringComparison.Ordinal));
        var host = new SampleHost(new LineCollectingWriter(lines), input, interactive: true);
        var sessionLaunch = Task.FromResult(broker.Handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        var parsed = new WatchArguments(WatchVerb.Watch, [new VolumeKey('T')])
        {
            CacheDirectory = Path.Combine(_directory, "cache")
        };

        var result = await host.RunThroughBrokerAsync(parsed, session,
            options => SyntheticVolumeFacts.Apply(options, 4242, volume: new VolumeKey('T'), volumeRootDirectory: _directory),
            CancellationToken.None);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.AreEqual(1, scans);
        Assert.Contains(expectedTemplate.Replace("{source}", source, StringComparison.Ordinal), lines);
        Assert.Contains(line => line.StartsWith("Saw 0 changes", StringComparison.Ordinal), lines);
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> QuietWatch(
        UsnJournalCursor since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return ([], since);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

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

        var result = await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('C')]), session, ResolveOptions, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.Contains("Index holds 2 rows; 0 records skipped", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("Catch-up held; watch supported: True", lines);
        Assert.Contains(line => line.StartsWith("  C: Finished: ", StringComparison.Ordinal), lines);
        Assert.Contains("=== Root C: done ===", lines);
    }

    [TestMethod]
    public async Task Scan_PrintsTheProducerAndTheUnavailableReason()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var sessionLaunch = Task.FromResult(broker.Handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        // The volume is not NTFS, so Automatic walks the directory and says why.
        var result = await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('C')]), session,
            options => SyntheticVolumeFacts.Apply(options, 4242, isNtfs: false, volume: new VolumeKey('C'),
                volumeRootDirectory: _directory), CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.Contains("producer Enumeration; MFT unavailable: NotNtfs", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_Scan_SeveralRoots_LaunchesOneBrokerForTheWholeRun()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var handle = broker.Handle;
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('C'), new VolumeKey('D')]), session, ResolveOptions, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Index holds ", StringComparison.Ordinal)));
        Assert.Contains("=== Root D: done ===", lines);
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

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('C')]), session, ResolveOptions, CancellationToken.None);

        Assert.Contains(line => line.StartsWith("Index holds ", StringComparison.Ordinal), lines);
        Assert.Contains("Catch-up lost: CheckpointTrimmed", lines);
    }

    [TestMethod]
    public async Task ScanThroughBroker_ScanFailsOnTheBroker_PrintsTheProducerFailure()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) => throw new IOException("volume unreadable")
        }));
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var sessionLaunch = Task.FromResult(broker.Handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('C')]), session, ResolveOptions, CancellationToken.None);

        Assert.Contains(line => line.StartsWith("Error on root C: ", StringComparison.Ordinal) &&
                                        line.Contains("volume unreadable"), lines, string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.Contains("done"), lines);
    }

    [TestMethod]
    public async Task ResolveOptions_SyntheticRoots_RootAtTheFixtureDirectoryNotARealVolume()
    {
        foreach (var letter in new[] { 'C', 'D', 'Z' })
        {
            var options = ResolveOptions(new FileIndexOptions { Root = new VolumeKey(letter), CacheMode = CacheMode.Disabled });

            await using var index = await FileIndex.OpenAsync(options with { Producer = ProducerSelection.WalkOnly }, CancellationToken.None);

            Assert.AreEqual(RootState.Ready, index.Status.State);
        }
    }

    [TestMethod]
    public async Task ScanThroughBroker_RootRootMissing_ReportsOfflineWithoutSuccessLines()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var lines = new List<string>();
        var scanner = ScannerOverBroker(lines);
        var handle = broker.Handle;
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('Q')]), session, options => SyntheticVolumeFacts.Apply(options, 4242, volume: new VolumeKey('Q'), volumeRootDirectory: Path.Combine(_directory, "missing-root")), CancellationToken.None);

        Assert.Contains("Error on root Q: The root is offline; nothing was scanned.", lines,
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

        await scanner.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Scan, [new VolumeKey('C')]), session, ResolveOptions, CancellationToken.None);

        Assert.Contains(line => line.StartsWith("Error on root C: ", StringComparison.Ordinal) &&
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

    // Each volume roots at its own directory: the members of a set must not nest.
    FileIndexOptions ResolveOptions(FileIndexOptions options)
    {
        var letter = options.Root!.Value.ToString()[0];
        var rootDirectory = Path.Combine(_directory, letter.ToString());
        Directory.CreateDirectory(rootDirectory);
        return SyntheticVolumeFacts.Apply(options, 4242, volume: new VolumeKey(letter), volumeRootDirectory: rootDirectory);
    }
}
