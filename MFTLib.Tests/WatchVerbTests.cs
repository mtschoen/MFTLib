using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// The Watch sample's index verbs over an in-process broker with scripted volumes: watch, rescan, journal and cache,
// plus the elevation report that touches no volume.
[TestClass]
[DoNotParallelize]
public class WatchVerbTests
{
    static readonly SyntheticJournalCursor Armed = new(7, 1000);

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"watchVerbs-{Guid.NewGuid():N}");
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

    [TestMethod]
    [Timeout(60000)]
    public async Task Run_Watch_PrintsStateChangesAndEachChangeWithItsKind()
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
        host._delay = (_, cancellationToken) => seen.Task.WaitAsync(cancellationToken);

        var result = host.Run(["watch", "X", "--seconds", "30"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("  watch X: CaughtUp", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  Created: ", StringComparison.Ordinal) && line.EndsWith("new.txt", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Saw 1 changes in 30 seconds;", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("Broker: connected."));
        Assert.IsTrue(lines.Contains("  start X: Succeeded"), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("  stop X: Succeeded"), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_Rescan_ScansAgainAndReportsTheCacheState()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();

        var result = HostOver(handle, lines.Add).Run(["rescan", "X"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Index holds ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("=== Drive X: done ==="));
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Cache X: block ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Watch: NotStarted v", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.Contains("recovery stopped False", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_JournalWithBothSizes_GrowsTheJournalThroughTheBroker()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes() with
        {
            GrowUsnJournal = (_, maximum, delta) => new UsnJournalSettings { MaximumSize = maximum, AllocationDelta = delta }
        });
        var lines = new List<string>();

        var result = HostOver(handle, lines.Add).Run(["journal", "X", "--maximum-size", "9000", "--allocation-delta", "4096"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Journal X: maximum size 9000, allocation delta 4096"), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_CacheAfterACachedOpen_ListsTheBlockAndClearRemovesIt()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        HostOver(handle, _ => { }).Run(["rescan", "X"]);
        var cacheDirectory = Path.Combine(_directory, "cache");
        var listing = new List<string>();
        var clearing = new List<string>();
        var afterwards = new List<string>();

        Assert.AreEqual(0, HostOver(handle, listing.Add).Run(["cache", "--cache-directory", cacheDirectory]));
        Assert.AreEqual(0, HostOver(handle, clearing.Add).Run(["cache", "X", "--cache-directory", cacheDirectory, "--clear"]));
        Assert.AreEqual(0, HostOver(handle, afterwards.Add).Run(["cache", "--cache-directory", cacheDirectory]));

        Assert.AreEqual(1, listing.Count(line => line.StartsWith("X: ", StringComparison.Ordinal)), string.Join(Environment.NewLine, listing));
        Assert.IsTrue(listing.Any(line => line.StartsWith("X: ", StringComparison.Ordinal) && line.Contains(", policy policy-", StringComparison.Ordinal)), "The block lives in its policy folder and the listing names it.");
        Assert.IsTrue(clearing.Any(line => line.Trim() == "X: Deleted"), string.Join(Environment.NewLine, clearing));
        Assert.AreEqual(0, afterwards.Count(line => line.StartsWith("X: ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_ElevationStatus_ReportsTheProcessWithoutLaunchingAnything()
    {
        var lines = new List<string>();
        var host = new SampleHost
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _getProcessPath = () => "tool.exe",
            _getEnvironmentVariable = _ => null,
            _createBrokerSession = () => throw new AssertFailedException("No broker may launch."),
            _writeLine = lines.Add
        };

        var result = host.Run(["elevation-status"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Process: tool.exe"));
        Assert.IsTrue(lines.Contains("Elevated: False; can self-elevate: True; unattended: False"), string.Join(Environment.NewLine, lines));
    }

    // The run is attended and unelevated, so the heads-up dialog precedes the broker launch.
    SampleHost HostOver(InProcessBrokerHandle handle, Action<string> writeLine)
    {
        var host = new SampleHost
        {
            _isElevated = () => false,
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
            ScanDrive = _ =>
            [
                [
                    new SyntheticScanRecord { RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true },
                    new SyntheticScanRecord { RecordNumber = 6, ParentRecordNumber = 5, FileName = "file.txt" }
                ]
            ]
        };
    }

    static async IAsyncEnumerable<(SyntheticJournalRecord[] Entries, SyntheticJournalCursor Cursor)> OneBatchThenQuiet(
        SyntheticJournalRecord[] entries, SyntheticJournalCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return (entries, cursor);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}
