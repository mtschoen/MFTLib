using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The Index verbs over the broker source, served by an in-process broker so nothing elevates. The heads-up
// dialog is scripted to a deliberate OK, as it is for scan-drive.
[TestClass]
[DoNotParallelize]
public class DriveScannerIndexBrokerTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);

    string _directory = null!;
    string _originalLogDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"indexBrokerVerbs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _originalLogDirectory = BrokerDiagnostics.LogDirectory;
    }

    [TestCleanup]
    public void Cleanup()
    {
        UsnJournalSettingsQuery._queryOverride = null;
        BrokerDiagnostics.LogDirectory = _originalLogDirectory;
        BrokerDiagnostics.ResetToDefaults();
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
    public async Task Search_OverTheBroker_PrintsTheSessionEventsAndTheProfile()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();

        var result = ScannerOver(broker, lines).Run(["search", "C", "--name", "file", "--no-cache"]);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("  broker profile full; keep names []"), Joined(lines));
        Assert.IsTrue(lines.Contains("  broker: connecting"), Joined(lines));
        Assert.IsTrue(lines.Contains("  broker: connected"), Joined(lines));
        Assert.IsTrue(lines.Any(line => line.Contains("file.txt [name file.txt;")), Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Drive C: state Ready;", StringComparison.Ordinal)), Joined(lines));
    }

    [TestMethod]
    public async Task Search_DirectoriesProfileWithKeepNames_PrintsThem()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();

        ScannerOver(broker, lines).Run(["search", "C", "--no-cache", "--profile", "directories", "--keep-name", "file.txt"]);

        Assert.IsTrue(lines.Contains("  broker profile directories; keep names [file.txt]"), Joined(lines));
    }

    [TestMethod]
    public async Task Search_Diagnostics_EnablesTheBrokerLogAndReadsTheDirectoryBack()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();
        var logDirectory = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logDirectory);

        ScannerOver(broker, lines).Run(["search", "C", "--no-cache", "--diagnostics", logDirectory]);

        Assert.IsTrue(lines.Contains($"Broker diagnostics enabled; the log directory reads back as {logDirectory}"), Joined(lines));
    }

    [TestMethod]
    public async Task Search_ConnectionTimeoutWithCacheOnly_BuildsTheLaunchCallbackSessionWithoutLaunching()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();

        var result = ScannerOver(broker, lines).Run(["search", "C", "--no-cache", "--cache-only", "--connection-timeout", "5"]);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("Broker session with a launch callback and a 5 second connection timeout."), Joined(lines));
        Assert.IsFalse(lines.Any(line => line.StartsWith("  broker: launching", StringComparison.Ordinal)),
            "A cache-only open never reaches the broker.");
    }

    [TestMethod]
    public async Task Watch_OverTheBroker_ReportsTheSessionAfterTeardown()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var lines = new List<string>();
        var scanner = ScannerOver(broker, lines);
        scanner._delay = (_, _) => Task.CompletedTask;

        var result = scanner.Run(["watch", "C", "--no-cache", "--seconds", "1", "--inspect-session"]);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Broker session after teardown: HasEnded True; Ended reported ", StringComparison.Ordinal)),
            Joined(lines));
    }

    [TestMethod]
    public async Task JournalGrow_GrowsThroughTheBrokerAndPrintsSizingBeforeAndAfter()
    {
        var received = new List<(string Drive, long MaximumSize, long AllocationDelta)>();
        await using var broker = new InProcessBroker(CreateHost(growUsnJournal: (drive, maximumSize, allocationDelta) =>
        {
            received.Add((drive, maximumSize, allocationDelta));
            return new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        }));
        var lines = new List<string>();
        var current = new UsnJournalSettings { MaximumSize = 1000, AllocationDelta = 100 };
        UsnJournalSettingsQuery._queryOverride = _ => current;
        var scanner = ScannerOver(broker, lines);
        scanner._getEnvironmentVariable = _ => null;

        var result = scanner.Run(["journal-grow", "C", "--maximum-size", "5000", "--allocation-delta", "500"]);

        Assert.AreEqual(0, result, Joined(lines));
        CollectionAssert.AreEqual(new[] { ("C", 5000L, 500L) }, received);
        Assert.IsTrue(lines.Contains("Before growing drive C:"));
        Assert.IsTrue(lines.Contains("  drive C: maximum size 1000 bytes, allocation delta 100 bytes"), Joined(lines));
        Assert.IsTrue(lines.Contains("The broker reports maximum size 5000 bytes, allocation delta 500 bytes"), Joined(lines));
        Assert.IsTrue(lines.Contains("After growing drive C:"));
    }

    [TestMethod]
    public async Task JournalGrow_TheVolumeRefuses_ReportsTheRefusal()
    {
        await using var broker = new InProcessBroker(CreateHost(growUsnJournal: (_, _, _) =>
            throw new InvalidOperationException("only growth is permitted")));
        var lines = new List<string>();
        UsnJournalSettingsQuery._queryOverride = _ => new UsnJournalSettings { MaximumSize = 1000, AllocationDelta = 100 };

        var result = ScannerOver(broker, lines).Run(["journal-grow", "C", "--maximum-size", "5", "--allocation-delta", "1"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error in journal-grow: ", StringComparison.Ordinal) &&
                                        line.Contains("only growth is permitted")), Joined(lines));
    }

    DriveScanner ScannerOver(InProcessBroker broker, List<string> lines)
    {
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _getEnvironmentVariable = _ => null,
            _createBrokerSession = () => BrokerTestHarness.CreateSession(_ => Task.FromResult(broker.Process)),
            _resolveDrive = letter => new IndexedDrive(char.ToUpperInvariant(letter[0]), _directory, 4242),
            _cacheDirectory = _directory,
            _writeLine = line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            }
        };
        DriveScannerElevationNoticeTests.AcknowledgeDeliberately(scanner);
        return scanner;
    }

    static JournalBrokerHost CreateHost(GrowUsnJournalQuery? growUsnJournal = null)
    {
        return new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                _ => Armed,
                (_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]],
                (_, since, _) => (Array.Empty<UsnJournalEntry>(), since),
                QueryVolumeInformation: _ => Volume,
                GrowUsnJournal: growUsnJournal),
            processorCount: 4);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }

    static string Joined(IEnumerable<string> lines)
    {
        return string.Join(Environment.NewLine, lines);
    }
}
