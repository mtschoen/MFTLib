using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// A bare launch of the Watch sample: the roots it chooses, the watch that runs until Enter, and the exit
// code when a root fails. The root list, the key reader and the delay are seams, so nothing waits on a clock.
[TestClass]
[DoNotParallelize]
public sealed class WatchFirstRunTests : IDisposable
{
    static readonly SyntheticJournalCursor Armed = new(7, 1000);

    TempDirectory _tempDirectory = null!;
    string TemporaryDirectoryPath => _tempDirectory.Path;
    UnattendedEnvironmentScope _attended = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _attended = new UnattendedEnvironmentScope(null);
        _tempDirectory = new TempDirectory("watchFirstRun");
    }

    public void Dispose()
    {
        _attended.Dispose();
        _tempDirectory.Dispose();
    }

    static WindowsVolume Facts(string rootDirectory, string? reason = null) =>
        SyntheticNotifications.CreateWindowsVolume(new VolumeKey(rootDirectory[0]), rootDirectory, DriveType.Fixed) with
        {
            IsReady = true,
            Format = reason is null ? "NTFS" : "exFAT",
            IsIndexable = reason is null,
            Reason = reason
        };

    [TestMethod]
    public void TryChooseDrives_NoArguments_CoversEveryChosenDriveAndSaysWhich()
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null);
        var parsed = new WatchArguments(WatchVerb.Watch, []);
        var available = new[]
        {
            Facts(@"G:\"), Facts(@"C:\"), Facts(@"h:\"), Facts(@"F:\", "not NTFS (exFAT)")
        };

        var result = host.TryChooseDrives(ref parsed, available);

        Assert.IsTrue(result);
        Assert.Contains(@"Skipping drive F:\ (Fixed, ready, format exFAT: not NTFS (exFAT))", lines);
        Assert.Contains("No root was named, so this run covers every ready local NTFS drive: C, G, H. Name roots to choose others.", lines, string.Join(Environment.NewLine, lines));
        Assert.AreSequenceEqual(["C", "G", "H"], [.. parsed.Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    public void TryChooseDrives_NamedDrives_AreNotReplacedByTheMachineList()
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null);
        var parsed = new WatchArguments(WatchVerb.Watch, [new VolumeKey('Z')]);

        var result = host.TryChooseDrives(ref parsed, []);

        Assert.IsTrue(result);
        Assert.DoesNotContain(line => line.StartsWith("No root was named", StringComparison.Ordinal), lines);
        Assert.AreSequenceEqual(["Z"], [.. parsed.Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    public void TryChooseDrives_NoReadyNtfsDrive_SaysSoAndReturnsFalse()
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null);
        var parsed = new WatchArguments(WatchVerb.Watch, []);

        var result = host.TryChooseDrives(ref parsed, [Facts(@"D:\", "optical")]);

        Assert.IsFalse(result);
        Assert.Contains(line => line.StartsWith("No root was named and no ready local NTFS drive", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void TryChooseDrives_CacheAndElevationStatusWithNoDrive_DoNotRequireDrives()
    {
        var host = new SampleHost(TextWriter.Null, TextReader.Null);
        var cacheParsed = new WatchArguments(WatchVerb.Cache, []);
        var elevationParsed = new WatchArguments(WatchVerb.ElevationStatus, []);

        Assert.IsTrue(host.TryChooseDrives(ref cacheParsed, []));
        Assert.IsTrue(host.TryChooseDrives(ref elevationParsed, []));
    }

    // What the composed Run hands to execution; Run itself never reaches a broker, a volume or a window.
    sealed class ExecutionProbe
    {
        public List<WatchArguments> Received { get; } = [];

        public int Execute(WatchArguments request)
        {
            Received.Add(request);
            return 0;
        }
    }

    // A lazy list: it fails the test only if the host enumerates it.
    static IEnumerable<WindowsVolume> MachineThatMustNotBeListed() =>
        Enumerable.Range(0, 1).Select<int, WindowsVolume>(_ =>
            throw new AssertFailedException("These runs name their volumes or touch no volume, so the machine is not listed."));

    [TestMethod]
    public void Run_NoArguments_ChosenRootsReachExecutionWithAResolvedCacheDirectory()
    {
        var lines = new List<string>();
        var probe = new ExecutionProbe();
        var host = HostOver(lines);
        WindowsVolume[] machine = [Facts(@"G:\"), Facts(@"C:\"), Facts(@"h:\"), Facts(@"F:\", "not NTFS (exFAT)")];

        var result = host.Run([], machine, probe.Execute);

        Assert.AreEqual(0, result);
        var received = probe.Received.Single();
        Assert.AreEqual(WatchVerb.Watch, received.Verb);
        Assert.AreSequenceEqual(["C", "G", "H"], [.. received.Roots.Select(root => root.ToString())]);
        Assert.IsFalse(string.IsNullOrEmpty(received.CacheDirectory), "A cached verb reaches execution with its cache directory resolved.");
        Assert.Contains("No root was named, so this run covers every ready local NTFS drive: C, G, H. Name roots to choose others.", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_NamedRoots_ReachExecutionAndTheMachineIsNeverListed()
    {
        var probe = new ExecutionProbe();

        var result = HostOver([]).Run(["scan", "Z"], MachineThatMustNotBeListed(), probe.Execute);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["Z"], [.. probe.Received.Single().Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    public void Run_NoReadyNtfsRoot_ExitsOneBeforeElevationAndExecution()
    {
        var lines = new List<string>();
        var probe = new ExecutionProbe();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null, new FakeTimeProvider())
        {
            _isElevated = () => throw new AssertFailedException("Nothing to scan must not reach elevation."),
            _canSelfElevate = () => throw new AssertFailedException("Nothing to scan must not reach elevation.")
        };

        var result = host.Run([], [Facts(@"D:\", "optical")], probe.Execute);

        Assert.AreEqual(1, result);
        Assert.IsEmpty(probe.Received);
        Assert.Contains(line => line.StartsWith("No root was named and no ready local NTFS drive", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void Run_CacheAndElevationStatusWithNoRoot_ReachExecutionWithoutListingTheMachine()
    {
        var probe = new ExecutionProbe();
        var host = HostOver([]);

        Assert.AreEqual(0, host.Run(["elevation-status"], MachineThatMustNotBeListed(), probe.Execute));
        Assert.AreEqual(0, host.Run(["cache", "--cache-directory", Path.Combine(TemporaryDirectoryPath, "none")], MachineThatMustNotBeListed(), probe.Execute));

        Assert.AreSequenceEqual([WatchVerb.ElevationStatus, WatchVerb.Cache], [.. probe.Received.Select(request => request.Verb)]);
    }

    [TestMethod]
    public void Run_BrokerLaunchNeededButUnattended_NeverReachesExecution()
    {
        using var unattended = new UnattendedEnvironmentScope("1");
        var probe = new ExecutionProbe();
        var host = new SampleHost(TextWriter.Null, TextReader.Null, new FakeTimeProvider())
        {
            _isElevated = () => false
        };

        var result = host.Run(["scan", "C"], MachineThatMustNotBeListed(), probe.Execute);

        Assert.AreEqual(1, result);
        Assert.IsEmpty(probe.Received, "An unattended run never launches the broker.");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Run_BareLaunch_WatchesUntilEnterIsPressedAndPrintsChangesMeanwhile()
    {
        var created = new SyntheticJournalRecord
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            Usn = 1200,
            FileName = "new.txt",
            Reason = SyntheticJournalReason.FileCreate | SyntheticJournalReason.Close
        };
        await using var handle = BrokerTestHarness.StartInProcess(Volumes() with
        {
            WatchVolume = (_, _, cancellationToken) => ScriptedJournalStreams.OneBatchThenQuiet([created], new SyntheticJournalCursor(7, 1500), cancellationToken)
        });
        var lines = new List<string>();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var notifyingWriter = new LineCollectingWriter(lines, line =>
        {
            if (line.StartsWith("  Created:", StringComparison.Ordinal))
            {
                seen.TrySetResult();
            }
        });
        using var keyReader = new GateLineReader(seen.Task);
        var host = HostOver(notifyingWriter, keyReader, interactive: true);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, [new VolumeKey('X')]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureOptions, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.Contains("Watching X. Type add-root <path> to watch another root, or press Enter to stop.", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains(line => line.StartsWith("Saw 1 changes until Enter was pressed;", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("  stop X: Succeeded", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Run_CancelledWhileWaitingForALine_StopsWithoutInput()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var neverPressed = new TaskCompletionSource();
        // The cancellation lands while the read is blocked, the way a stopped run meets a console nobody touches.
        using var keyReader = new GateLineReader(neverPressed.Task, cancellation.Cancel);
        var host = HostOver(TextWriter.Null, keyReader, interactive: true);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        try
        {
            var run = host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, [new VolumeKey('X')]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureOptions, cancellation.Token);

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => run);
        }
        finally
        {
            // Lets the abandoned read return, so no thread outlives the test.
            neverPressed.SetResult();
        }
    }

    [TestMethod]
    [DataRow(true, true, DisplayName = "unattended")]
    [DataRow(false, false, DisplayName = "input redirected")]
    public async Task Run_BareLaunch_NobodyAtTheKeyboard_WatchesForTheDefaultSecondsInstead(bool unattended, bool interactive)
    {
        using var scope = new UnattendedEnvironmentScope(unattended ? "1" : null);
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var clock = new AutoAdvanceClock(TimeSpan.FromSeconds(WatchArguments.DefaultSeconds));
        var host = HostOver(lines, new FailingReader("Nobody is at the keyboard."), timeProvider: clock, interactive: interactive);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, [new VolumeKey('X')]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureOptions, TestContext.CancellationToken);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.AreEqual(TimeSpan.FromSeconds(WatchArguments.DefaultSeconds), clock.TotalAdvanced);
        Assert.Contains(line => line.StartsWith("Saw 0 changes in 10 seconds;", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_SecondsGiven_WinsOverInteractiveCommands()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var clock = new AutoAdvanceClock(TimeSpan.FromSeconds(3));
        var host = HostOver(TextWriter.Null, new FailingReader("--seconds replaces interactive commands."), timeProvider: clock, interactive: true);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, [new VolumeKey('X')]) { Seconds = 3, CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureOptions, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.AreEqual(TimeSpan.FromSeconds(3), clock.TotalAdvanced);
    }

    [TestMethod]
    public async Task Run_OneOfTwoRootsFails_ExitsOneAndStillWatchesTheOther()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var clock = new AutoAdvanceClock(TimeSpan.FromSeconds(WatchArguments.DefaultSeconds));
        var host = HostOver(lines, timeProvider: clock, interactive: false);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(
            new WatchArguments(WatchVerb.Watch, [new VolumeKey('X'), new VolumeKey('Q')]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") },
            session,
            FixtureOptions,
            CancellationToken.None);

        Assert.AreEqual(1, result, string.Join(Environment.NewLine, lines));
        Assert.Contains("Error on root Q: The root is offline; nothing was scanned.", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains(line => line.StartsWith("Saw 0 changes in 10 seconds;", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.StartsWith("Completed at", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public async Task Run_WatchOfOnlyFailingRoots_WaitsForNothingAndExitsOne()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var host = HostOver(lines, new FailingReader("There is no root to watch, so no key is awaited."));
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(
            new WatchArguments(WatchVerb.Watch, [new VolumeKey('Q')]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") },
            session,
            FixtureOptions,
            CancellationToken.None);

        Assert.AreEqual(1, result);
        Assert.DoesNotContain(line => line.StartsWith("Watching", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_ScanOfOneFailingRoot_ExitsOne()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var host = HostOver(TextWriter.Null);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(
            new WatchArguments(WatchVerb.Scan, [new VolumeKey('Q')]),
            session,
            FixtureOptions,
            CancellationToken.None);

        Assert.AreEqual(1, result);
    }

    // Each root roots at its own directory under the fixture (members of a set must not nest), except Q, whose
    // root is missing, so it is offline.
    FileIndexOptions FixtureOptions(FileIndexOptions options)
    {
        var letter = options.Root!.Value.ToString()[0];
        var rootDirectory = Path.Combine(TemporaryDirectoryPath, letter == 'Q' ? "missing-root" : letter.ToString());
        if (letter != 'Q')
        {
            Directory.CreateDirectory(rootDirectory);
        }

        return SyntheticVolumeFacts.Apply(options, 4242, volume: new VolumeKey(letter), volumeRootDirectory: rootDirectory);
    }

    static SampleHost HostOver(List<string> lines, TextReader? input = null, TimeProvider? timeProvider = null, bool? interactive = null) =>
        HostOver(new LineCollectingWriter(lines), input, timeProvider, interactive);

    static SampleHost HostOver(
        TextWriter output,
        TextReader? input = null,
        TimeProvider? timeProvider = null,
        bool? interactive = null)
    {
        var host = new SampleHost(output, input ?? TextReader.Null, timeProvider ?? new FakeTimeProvider(), interactive)
        {
            _isElevated = () => false
        };
        WatchNoticeSupport.AcknowledgeDeliberately(host);
        return host;
    }

    sealed class GateLineReader(Task gate, Action? onRead = null) : TextReader
    {
        public override int Read()
        {
            onRead?.Invoke();
            gate.GetAwaiter().GetResult();
            return '\n';
        }
    }

    static ScriptedBrokerVolumes Volumes()
    {
        return new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => Armed,
            WatchVolume = (_, _, cancellationToken) => ScriptedJournalStreams.OneBatchThenQuiet([], Armed, cancellationToken),
            ScanVolume = _ =>
            [
                [
                    new SyntheticScanRecord { RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true },
                    new SyntheticScanRecord { RecordNumber = 6, ParentRecordNumber = 5, FileName = "file.txt" }
                ]
            ]
        };
    }
}
