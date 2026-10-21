using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// A bare launch of the Watch sample: the drives it chooses, the watch that runs until a key press, and the exit
// code when a drive fails. The drive list, the key reader and the delay are seams, so nothing waits on a clock.
[TestClass]
[DoNotParallelize]
public sealed class WatchFirstRunTests : IDisposable
{
    static readonly SyntheticJournalCursor Armed = new(7, 1000);

    TempDirectory _tempDirectory = null!;
    string TemporaryDirectoryPath => _tempDirectory.Path;
    UnattendedEnvironmentScope _attended = null!;

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
        new(new DriveKey(rootDirectory[0]), rootDirectory, DriveType.Fixed, true, reason is null ? "NTFS" : "exFAT",
            reason is null, reason);

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
        CollectionAssert.Contains(lines, @"Skipping drive F:\ (Fixed, ready, format exFAT: not NTFS (exFAT))");
        Assert.IsTrue(lines.Contains("No drive was named, so this run covers every ready NTFS fixed drive: C, G, H. Name drives to choose others."), string.Join(Environment.NewLine, lines));
        CollectionAssert.AreEqual(new[] { "C", "G", "H" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryChooseDrives_NamedDrives_AreNotReplacedByTheMachineList()
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null);
        var parsed = new WatchArguments(WatchVerb.Watch, ["Z"]);

        var result = host.TryChooseDrives(ref parsed, []);

        Assert.IsTrue(result);
        Assert.IsFalse(lines.Any(line => line.StartsWith("No drive was named", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "Z" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryChooseDrives_NoReadyNtfsDrive_SaysSoAndReturnsFalse()
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null);
        var parsed = new WatchArguments(WatchVerb.Watch, []);

        var result = host.TryChooseDrives(ref parsed, [Facts(@"D:\", "optical")]);

        Assert.IsFalse(result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("No drive was named and no ready NTFS fixed drive", StringComparison.Ordinal)));
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
            throw new AssertFailedException("These runs name their drives or touch no volume, so the machine is not listed."));

    [TestMethod]
    public void Run_NoArguments_ChosenDrivesReachExecutionWithAResolvedCacheDirectory()
    {
        var lines = new List<string>();
        var probe = new ExecutionProbe();
        var host = HostOver(lines);
        WindowsVolume[] machine = [Facts(@"G:\"), Facts(@"C:\"), Facts(@"h:\"), Facts(@"F:\", "not NTFS (exFAT)")];

        var result = host.Run([], machine, probe.Execute);

        Assert.AreEqual(0, result);
        var received = probe.Received.Single();
        Assert.AreEqual(WatchVerb.Watch, received.Verb);
        CollectionAssert.AreEqual(new[] { "C", "G", "H" }, received.Drives.ToArray());
        Assert.IsFalse(string.IsNullOrEmpty(received.CacheDirectory), "A cached verb reaches execution with its cache directory resolved.");
        Assert.IsTrue(lines.Contains("No drive was named, so this run covers every ready NTFS fixed drive: C, G, H. Name drives to choose others."), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_NamedDrives_ReachExecutionAndTheMachineIsNeverListed()
    {
        var probe = new ExecutionProbe();

        var result = HostOver(new List<string>()).Run(["scan", "Z"], MachineThatMustNotBeListed(), probe.Execute);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "Z" }, probe.Received.Single().Drives.ToArray());
    }

    [TestMethod]
    public void Run_NoReadyNtfsDrive_ExitsOneBeforeElevationAndExecution()
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
        Assert.AreEqual(0, probe.Received.Count);
        Assert.IsTrue(lines.Any(line => line.StartsWith("No drive was named and no ready NTFS fixed drive", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_CacheAndElevationStatusWithNoDrive_ReachExecutionWithoutListingTheMachine()
    {
        var probe = new ExecutionProbe();
        var host = HostOver(new List<string>());

        Assert.AreEqual(0, host.Run(["elevation-status"], MachineThatMustNotBeListed(), probe.Execute));
        Assert.AreEqual(0, host.Run(["cache", "--cache-directory", Path.Combine(TemporaryDirectoryPath, "none")], MachineThatMustNotBeListed(), probe.Execute));

        CollectionAssert.AreEqual(new[] { WatchVerb.ElevationStatus, WatchVerb.Cache }, probe.Received.Select(request => request.Verb).ToArray());
    }

    [TestMethod]
    public void Run_BrokerLaunchNeededButUnattended_NeverReachesExecution()
    {
        using var unattended = new UnattendedEnvironmentScope("1");
        var probe = new ExecutionProbe();
        var host = new SampleHost(TextWriter.Null, TextReader.Null, new FakeTimeProvider())
        {
            _isElevated = () => false,
        };

        var result = host.Run(["scan", "C"], MachineThatMustNotBeListed(), probe.Execute);

        Assert.AreEqual(1, result);
        Assert.AreEqual(0, probe.Received.Count, "An unattended run never launches the broker.");
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Run_BareLaunch_WatchesUntilAKeyIsPressedAndPrintsChangesMeanwhile()
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
            WatchDrive = (_, _, cancellationToken) => ScriptedJournalStreams.OneBatchThenQuiet([created], new SyntheticJournalCursor(7, 1500), cancellationToken)
        });
        var lines = new List<string>();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var notifyingWriter = new LineCollectingWriter(lines, line =>
        {
            if (line.StartsWith("  Created:", StringComparison.Ordinal))
            {
                seen.TrySetResult();
            }
        });
        using var keyReader = new GateKeyReader(seen.Task);
        var host = HostOver(notifyingWriter, keyReader, interactive: true);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, ["X"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureDrive, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Watching X. Press any key to stop."), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Saw 1 changes until a key was pressed;", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("  stop X: Succeeded"), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Run_CancelledWhileWaitingForAKey_StopsWithoutTheKey()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        using var cancellation = new CancellationTokenSource();
        var neverPressed = new TaskCompletionSource();
        // The cancellation lands while the read is blocked, the way a stopped run meets a console nobody touches.
        using var keyReader = new GateKeyReader(neverPressed.Task, cancellation.Cancel);
        var host = HostOver(TextWriter.Null, keyReader, interactive: true);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        try
        {
            var run = host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, ["X"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureDrive, cancellation.Token);

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

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, ["X"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureDrive, CancellationToken.None);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.AreEqual(TimeSpan.FromSeconds(WatchArguments.DefaultSeconds), clock.TotalAdvanced);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Saw 0 changes in 10 seconds;", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_SecondsGiven_WinsOverTheKeyPress()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var clock = new AutoAdvanceClock(TimeSpan.FromSeconds(3));
        var host = HostOver(TextWriter.Null, new FailingReader("--seconds replaces the key press."), timeProvider: clock, interactive: true);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, ["X"]) { Seconds = 3, CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, session, FixtureDrive, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.AreEqual(TimeSpan.FromSeconds(3), clock.TotalAdvanced);
    }

    [TestMethod]
    public async Task Run_OneOfTwoDrivesFails_ExitsOneAndStillWatchesTheOther()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var clock = new AutoAdvanceClock(TimeSpan.FromSeconds(WatchArguments.DefaultSeconds));
        var host = HostOver(lines, timeProvider: clock, interactive: false);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(
            new WatchArguments(WatchVerb.Watch, ["X", "Q"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") },
            session,
            FixtureDrive,
            CancellationToken.None);

        Assert.AreEqual(1, result, string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("Error on drive Q: The drive is offline; nothing was scanned."), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Saw 0 changes in 10 seconds;", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(line => line.StartsWith("Completed at", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Run_WatchOfOnlyFailingDrives_WaitsForNothingAndExitsOne()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var host = HostOver(lines, new FailingReader("There is no drive to watch, so no key is awaited."));
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(
            new WatchArguments(WatchVerb.Watch, ["Q"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") },
            session,
            FixtureDrive,
            CancellationToken.None);

        Assert.AreEqual(1, result);
        Assert.IsFalse(lines.Any(line => line.StartsWith("Watching", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_ScanOfOneFailingDrive_ExitsOne()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var host = HostOver(TextWriter.Null);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);

        var result = await host.RunThroughBrokerAsync(
            new WatchArguments(WatchVerb.Scan, ["Q"]),
            session,
            FixtureDrive,
            CancellationToken.None);

        Assert.AreEqual(1, result);
    }

    // Every drive roots at the fixture directory except Q, whose root is missing, so it is offline.
    IndexedDrive FixtureDrive(string letter) =>
        new(letter[0], letter == "Q" ? Path.Combine(TemporaryDirectoryPath, "missing-root") : TemporaryDirectoryPath, 4242);

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
            _isElevated = () => false,
        };
        WatchNoticeSupport.AcknowledgeDeliberately(host);
        return host;
    }

    sealed class GateKeyReader(Task gate, Action? onRead = null) : TextReader
    {
        public override int Read()
        {
            onRead?.Invoke();
            gate.GetAwaiter().GetResult();
            return 'q';
        }
    }

    static ScriptedBrokerVolumes Volumes()
    {
        return new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => Armed,
            WatchDrive = (_, _, cancellationToken) => ScriptedJournalStreams.OneBatchThenQuiet([], Armed, cancellationToken),
            ScanDrive = _ =>
            [
                [
                    new SyntheticScanRecord { RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true },
                    new SyntheticScanRecord { RecordNumber = 6, ParentRecordNumber = 5, FileName = "file.txt" }
                ]
            ]
        };
    }
}
