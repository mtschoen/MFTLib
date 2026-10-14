using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// A bare launch of the Watch sample: the drives it chooses, the watch that runs until a key press, and the exit
// code when a drive fails. The drive list, the key reader and the delay are seams, so nothing waits on a clock.
[TestClass]
[DoNotParallelize]
public class WatchFirstRunTests
{
    static readonly SyntheticJournalCursor Armed = new(7, 1000);

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"watchFirstRun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A just-unmapped block file can stay locked briefly on Windows.
        }
    }

    internal static SampleHost.DriveFacts Facts(string name, DriveType type = DriveType.Fixed, bool ready = true, string? format = "NTFS") =>
        new(name, type, ready, ready ? format : null);

    [TestMethod]
    public void ReadyNtfsFixedDrives_KeepsOnlyReadyNtfsFixedLettersInOrder()
    {
        var chosen = SampleHost.ReadyNtfsFixedDrives(
        [
            Facts(@"G:\"),
            Facts(@"C:\"),
            Facts(@"D:\", DriveType.Removable),
            Facts(@"E:\", ready: false),
            Facts(@"F:\", format: "exFAT"),
            Facts(@"h:\", format: "ntfs"),
            Facts("/"),
            Facts(string.Empty)
        ]);

        CollectionAssert.AreEqual(new[] { "C", "G", "H" }, chosen.ToArray());
    }

    [TestMethod]
    public void ListDrivesNative_ReadsTheMachineWithoutFailing()
    {
        var chosen = SampleHost.ReadyNtfsFixedDrives(new SampleHost()._listDrives());

        Assert.IsTrue(chosen.All(letter => letter.Length == 1 && char.IsAsciiLetterUpper(letter[0])));
    }

    [TestMethod]
    public void Run_NoArguments_CoversEveryChosenDriveAndSaysWhich()
    {
        var lines = new List<string>();
        var host = new SampleHost
        {
            _listDrives = () => [Facts(@"C:\"), Facts(@"D:\")],
            _elevationNeed = _ => ElevationNeed.None,
            _createBrokerSession = () => throw new InvalidOperationException("stop here"),
            _writeLine = lines.Add
        };

        var result = host.Run([]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Contains("No drive was named, so this run covers every ready NTFS fixed drive: C, D. Name drives to choose others."), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_NamedDrives_AreNotReplacedByTheMachineList()
    {
        var lines = new List<string>();
        var host = new SampleHost
        {
            _listDrives = () => throw new AssertFailedException("A named drive must not list the machine."),
            _elevationNeed = _ => ElevationNeed.None,
            _createBrokerSession = () => throw new InvalidOperationException("stop here"),
            _writeLine = lines.Add
        };

        Assert.AreEqual(1, host.Run(["Z"]));
        Assert.IsFalse(lines.Any(line => line.StartsWith("No drive was named", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_NoReadyNtfsDrive_SaysSoAndExitsOneWithoutElevating()
    {
        var lines = new List<string>();
        var host = new SampleHost
        {
            _listDrives = () => [Facts(@"D:\", DriveType.CDRom, ready: false)],
            _isElevated = () => throw new AssertFailedException("Nothing to scan must not reach elevation."),
            _writeLine = lines.Add
        };

        Assert.AreEqual(1, host.Run([]));
        Assert.IsTrue(lines.Any(line => line.StartsWith("No drive was named and no ready NTFS fixed drive", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_CacheAndElevationStatusWithNoDrive_DoNotListTheMachine()
    {
        var host = new SampleHost
        {
            _listDrives = () => throw new AssertFailedException("These modes touch no volume."),
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _getProcessPath = () => "tool.exe",
            _getEnvironmentVariable = _ => null,
            _writeLine = _ => { }
        };

        Assert.AreEqual(0, host.Run(["elevation-status"]));
        Assert.AreEqual(0, host.Run(["cache", "--cache-directory", Path.Combine(_directory, "none")]));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Run_BareLaunch_WatchesUntilAKeyIsPressedAndPrintsChangesMeanwhile()
    {
        var created = new SyntheticJournalRecord
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            UpdateSequenceNumber = 1200,
            FileName = "new.txt",
            Reason = SyntheticJournalReason.FileCreate | SyntheticJournalReason.Close
        };
        await using var handle = BrokerTestHarness.StartInProcess(Volumes() with
        {
            WatchDrive = (_, _, cancellationToken) => OneBatchThenQuiet([created], new SyntheticJournalCursor(7, 1500), cancellationToken)
        });
        var lines = new List<string>();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = HostOver(handle, line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }

            if (line.StartsWith("  Created:", StringComparison.Ordinal))
            {
                seen.TrySetResult();
            }
        });
        host._isInteractive = () => true;
        host._delay = (_, _) => throw new AssertFailedException("An attended watch without --seconds waits for a key, not a delay.");
        host._readKey = () => seen.Task.GetAwaiter().GetResult();

        var result = host.Run([]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Watching X. Press any key to stop."), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Saw 1 changes until a key was pressed;", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("  stop X: Succeeded"), string.Join(Environment.NewLine, lines));
    }

    [DataTestMethod]
    [DataRow(true, true, DisplayName = "unattended")]
    [DataRow(false, false, DisplayName = "input redirected")]
    public async Task Run_BareLaunch_NobodyAtTheKeyboard_WatchesForTheDefaultSecondsInstead(bool unattended, bool interactive)
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var delays = new List<TimeSpan>();
        var host = HostOver(handle, lines.Add);
        host._getEnvironmentVariable = name => unattended && name == SampleHost.UnattendedVariableName ? "1" : null;
        host._isInteractive = () => interactive;
        host._readKey = () => throw new AssertFailedException("Nobody is at the keyboard.");
        host._delay = (span, _) =>
        {
            delays.Add(span);
            return Task.CompletedTask;
        };
        host._elevationNeed = _ => ElevationNeed.None;

        var result = host.Run([]);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(WatchArguments.DefaultSeconds) }, delays);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Saw 0 changes in 10 seconds;", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_SecondsGiven_WinsOverTheKeyPress()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var delays = new List<TimeSpan>();
        var host = HostOver(handle, _ => { });
        host._isInteractive = () => true;
        host._readKey = () => throw new AssertFailedException("--seconds replaces the key press.");
        host._delay = (span, _) =>
        {
            delays.Add(span);
            return Task.CompletedTask;
        };

        Assert.AreEqual(0, host.Run(["--seconds", "3"]));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, delays);
    }

    [TestMethod]
    public async Task Run_OneOfTwoDrivesFails_ExitsOneAndStillWatchesTheOther()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var host = HostOver(handle, lines.Add);
        host._listDrives = () => [Facts(@"X:\"), Facts(@"Q:\")];
        host._resolveDrive = letter => letter == "Q"
            ? new IndexedDrive('Q', Path.Combine(_directory, "missing-root"), 4242)
            : new IndexedDrive('X', _directory, 4242);
        host._isInteractive = () => false;
        host._delay = (_, _) => Task.CompletedTask;

        var result = host.Run([]);

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
        var host = HostOver(handle, lines.Add);
        host._resolveDrive = _ => new IndexedDrive('Q', Path.Combine(_directory, "missing-root"), 4242);
        host._isInteractive = () => true;
        host._readKey = () => throw new AssertFailedException("There is no drive to watch, so no key is awaited.");
        host._delay = (_, _) => throw new AssertFailedException("There is no drive to watch, so nothing waits.");

        Assert.AreEqual(1, host.Run(["watch", "Q"]));
        Assert.IsFalse(lines.Any(line => line.StartsWith("Watching", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_ScanDriveOfOneFailingDrive_ExitsOne()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var host = HostOver(handle, _ => { });
        host._resolveDrive = _ => new IndexedDrive('Q', Path.Combine(_directory, "missing-root"), 4242);

        Assert.AreEqual(1, host.Run(["scan-drive", "Q"]));
    }

    SampleHost HostOver(InProcessBrokerHandle handle, Action<string> writeLine)
    {
        var host = new SampleHost
        {
            _isElevated = () => false,
            _listDrives = () => [Facts(@"X:\")],
            _createBrokerSession = () => BrokerTestHarness.CreateSession(_ => Task.FromResult(handle)),
            _resolveDrive = letter => new IndexedDrive(char.ToUpperInvariant(letter[0]), _directory, 4242),
            _cacheDirectory = Path.Combine(_directory, "cache"),
            _writeLine = writeLine
        };
        WatchNoticeSupport.AcknowledgeDeliberately(host);
        return host;
    }

    static ScriptedBrokerVolumes Volumes()
    {
        return new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => Armed,
            WatchDrive = (_, _, cancellationToken) => OneBatchThenQuiet([], Armed, cancellationToken),
            ScanDrive = _ =>
            [
                [
                    new SyntheticScanRecord { RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true },
                    new SyntheticScanRecord { RecordNumber = 6, ParentRecordNumber = 5, FileName = "file.txt" }
                ]
            ]
        };
    }

    static async IAsyncEnumerable<(IReadOnlyList<SyntheticJournalRecord> Entries, SyntheticJournalCursor Cursor)> OneBatchThenQuiet(
        SyntheticJournalRecord[] entries, SyntheticJournalCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return (entries, cursor);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}
