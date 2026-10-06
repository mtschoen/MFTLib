using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The Index verbs: over an enumeration source (a real temp tree, no volume, no elevation) and over an
// in-process broker. Each test runs one command line through DriveScanner.Run and reads what it printed.
[TestClass]
[DoNotParallelize]
[SupportedOSPlatform("windows")] // The journal sizing seam is Windows-only, as are these TestProgram tests.
public class DriveScannerIndexVerbTests
{
    string _directory = null!;
    string _tree = null!;
    string _cache = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"indexVerbs-{Guid.NewGuid():N}");
        _tree = Path.Combine(_directory, "tree");
        _cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(Path.Combine(_tree, "docs"));
        Directory.CreateDirectory(Path.Combine(_tree, "other"));
        File.WriteAllText(Path.Combine(_tree, "docs", "readme.md"), "hello world");
        File.WriteAllBytes(Path.Combine(_tree, "docs", "big.bin"), new byte[5000]);
        File.WriteAllText(Path.Combine(_tree, "other", "readme.md"), "dup");
    }

    [TestCleanup]
    public void Cleanup()
    {
        UsnJournalSettingsQuery._queryOverride = null;
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
    public void Search_EveryFilter_IsEchoedAndNarrowsTheResult()
    {
        var docs = Path.Combine(_tree, "docs");

        var lines = Run(Enumerate("search", "--name", "*.md", "--case-sensitive", "--under", docs, "--files", "--min-size", "1",
            "--max-size", "100", "--after", "2000-01-01", "--before", "2100-01-01"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        StringAssert.Contains(Joined(lines), $"Query: name *.md (a glob with * or ?, else a substring), case sensitive True, under {docs}, directories False, size 1..100");
        Assert.IsTrue(lines.Contains("1 entries (limit 20)"), Joined(lines));
    }

    [TestMethod]
    public void Search_ExactStreamAndSubstring_AgreeOnTheTwoReadmeFiles()
    {
        Assert.IsTrue(Run(Enumerate("search", "--exact", "--name", "readme.md"), out _).Contains("2 entries (limit 20)"));
        Assert.IsTrue(Run(Enumerate("search", "--name", "readme", "--stream", "--limit", "1"), out _).Contains("1 entries streamed (limit 1)"));
        Assert.IsTrue(Run(Enumerate("search", "--name", "readme"), out _).Contains("2 entries (limit 20)"));
    }

    [TestMethod]
    public void Tree_ByPathAndByKey_WalksParentsAndChildren()
    {
        var readme = Path.Combine(_tree, "docs", "readme.md");
        var byPath = Run(Enumerate("tree", "--path", readme), out _);
        var key = System.Text.RegularExpressions.Regex.Match(byPath.First(line => line.StartsWith("Start:", StringComparison.Ordinal)), @"\[(S:\d+:Enumeration)").Groups[1].Value;

        var byKey = Run(Enumerate("tree", "--record-key", key), out _);
        var fromRoot = Run(Enumerate("tree", "--depth", "2"), out _);

        Assert.AreEqual(2, byPath.Count(line => line.StartsWith("  parent:", StringComparison.Ordinal)), Joined(byPath));
        Assert.IsTrue(byKey.Any(line => line.StartsWith($"Start: {readme} [{key}", StringComparison.Ordinal)), Joined(byKey));
        Assert.AreEqual(5, fromRoot.Count(line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains(" [S:", StringComparison.Ordinal)), Joined(fromRoot));
        Assert.IsTrue(Run(Enumerate("tree", "--path", Path.Combine(_tree, "none")), out _).Contains("Nothing found at that path or key."));
    }

    [TestMethod]
    public void OpenLargestAndDuplicates_ReadTheTreeBack()
    {
        var open = Run(Enumerate("open", "--path", Path.Combine(_tree, "docs", "readme.md"), "--count", "5"), out _);
        var largest = Run(Enumerate("largest", "--count", "1"), out _);
        var duplicates = Run(Enumerate("duplicate-names"), out _);

        Assert.IsTrue(open.Any(line => line.EndsWith("first 5 of 11 bytes: hello", StringComparison.Ordinal)), Joined(open));
        Assert.IsTrue(largest.Any(line => line.Contains("big.bin")), Joined(largest));
        Assert.IsTrue(duplicates.Any(line => line.StartsWith("readme.md: ", StringComparison.Ordinal)), Joined(duplicates));
    }

    [TestMethod]
    public void RescanAndWatch_UseEachScopeOverload()
    {
        var all = Run(Enumerate("rescan"), out _);
        var single = Run(Enumerate("rescan", "--drive-scope", "S"), out _);
        var list = Run(Enumerate("rescan", "--drive-list", "S"), out _);
        var delays = new List<TimeSpan>();
        var watch = Run(Enumerate("watch", "--seconds", "3", "--inspect-session"), out var result, scanner =>
            scanner._delay = (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        var watchSingle = Run(Enumerate("watch", "--drive-scope", "S"), out _, NoDelay);
        var watchList = Run(Enumerate("watch", "--drive-list", "S"), out _, NoDelay);

        Assert.IsTrue(all.Contains("  rescan S: Succeeded "), Joined(all));
        Assert.IsTrue(single.Contains("  rescan S: done"));
        Assert.IsTrue(list.Contains("  rescan S: Succeeded "));
        Assert.AreEqual(0, result, Joined(watch));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, delays);
        Assert.IsTrue(watch.Contains("  start S: NotApplicable "), Joined(watch));
        Assert.IsTrue(watchSingle.Any(line => line.StartsWith("  start failed: InvalidOperationException", StringComparison.Ordinal)), Joined(watchSingle));
        Assert.IsTrue(watchList.Contains("  stop S: NotApplicable "), Joined(watchList));
    }

    [TestMethod]
    public void Journal_PrintsSizingFromTheJournalQuery()
    {
        UsnJournalSettingsQuery._queryOverride = _ => new UsnJournalSettings { MaximumSize = 1000, AllocationDelta = 100 };

        var lines = Run(Enumerate("journal"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("Journal S: maximum 1000, allocation delta 100"), Joined(lines));
    }

    [TestMethod]
    public void Cache_WrittenInspectedAndCleared()
    {
        Run(["search", "S", "--source", "enumeration", "--root", _tree, "--cache-directory", _cache, "--cache-tag", "TEST:1"], out _);

        var inspected = Run(["cache", "--cache-directory", _cache, "--cache-tag", "TEST:1", "S"], out var result);
        var other = Run(["cache", "--cache-directory", _cache, "--cache-tag", "TEST:2"], out _);
        var cleared = Run(["cache", "--cache-directory", _cache, "--clear"], out _);
        var created = Run(["cache", "--cache-directory", Path.Combine(_directory, "fresh"), "--ensure-created"], out _);
        File.WriteAllText(Path.Combine(_cache, "T-00000002.mlix"), "not a block");
        var invalid = Run(["cache", "--cache-directory", _cache], out _);

        Assert.AreEqual(0, result, Joined(inspected));
        Assert.IsTrue(inspected.Any(line => line.Contains("Available Valid Enumeration") && line.EndsWith("(equals \"TEST\" v1: True)", StringComparison.Ordinal)), Joined(inspected));
        Assert.IsTrue(other.Any(line => line.EndsWith("(equals \"TEST\" v2: False)", StringComparison.Ordinal)), Joined(other));
        Assert.IsTrue(cleared.Any(line => line.EndsWith(": Deleted ", StringComparison.Ordinal)), Joined(cleared));
        Assert.IsTrue(created.Any(line => line.StartsWith("Cache directory ", StringComparison.Ordinal)));
        Assert.IsTrue(invalid.Any(line => line.Contains("Invalid WrongMagic")), Joined(invalid));
    }

    [TestMethod]
    public void UnavailableSource_AnswersFromAWarmCacheOnly()
    {
        Run(["search", "S", "--source", "enumeration", "--root", _tree, "--cache-directory", _cache, "--cache-tag", "TEST:1"], out _);

        var lines = Run(["search", "S", "--source", "unavailable", "--root", _tree, "--cache-directory", _cache, "--cache-tag", "TEST:1",
            "--cache-only"], out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("6 entries (limit 20)"), Joined(lines));
    }

    [TestMethod]
    public void ElevationStatusAndBadCommandLines()
    {
        var status = Run(["elevation-status"], out var result);
        var bad = Run(["search", "S", "--bogus"], out var badResult);
        var missing = Run(["search", "S", "--source", "enumeration"], out var missingResult);
        var brokerUnattended = Run(["search", "S"], out var brokerResult, scanner =>
            scanner._getEnvironmentVariable = name => name == DriveScanner.UnattendedVariableName ? "1" : null);

        Assert.AreEqual(0, result);
        Assert.IsTrue(status[0].StartsWith("Elevated ", StringComparison.Ordinal));
        Assert.AreEqual(2, badResult, Joined(bad));
        Assert.AreEqual(1, missingResult, Joined(missing));
        Assert.AreEqual(1, brokerResult, Joined(brokerUnattended));
        Assert.IsTrue(brokerUnattended.Any(line => line.StartsWith("Running unattended", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BrokerSource_Search_PrintsTheSessionEventsAndTheProfile()
    {
        await using var broker = NewBroker([]);
        var logs = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logs);

        var search = Run(["search", "C", "--no-cache", "--profile", "directories", "--keep-name", "file.txt", "--diagnostics", logs],
            out var result, UseBroker(broker));

        Assert.AreEqual(0, result, Joined(search));
        StringAssert.Contains(Joined(search), "profile DirectoryIndex");
        StringAssert.Contains(Joined(search), $"Broker diagnostics log directory {logs}");
    }

    [TestMethod]
    public async Task BrokerSource_Watch_ReportsTheSessionAfterTeardown()
    {
        await using var broker = NewBroker([]);

        var watch = Run(["watch", "C", "--no-cache", "--inspect-session"], out _, UseBroker(broker));

        Assert.IsTrue(watch.Contains("  broker connected"), Joined(watch));
        Assert.IsTrue(watch.Any(line => line.StartsWith("Session after teardown: has ended True", StringComparison.Ordinal)), Joined(watch));
    }

    [TestMethod]
    public async Task BrokerSource_JournalGrow_GrowsThroughTheBrokerAndReadsSizingBeforeAndAfter()
    {
        var grown = new List<(string Drive, long Maximum, long Delta)>();
        await using var broker = NewBroker(grown);
        UsnJournalSettingsQuery._queryOverride = _ => new UsnJournalSettings { MaximumSize = 1000, AllocationDelta = 100 };

        var grow = Run(["journal", "C", "--maximum-size", "5000", "--allocation-delta", "500"], out var result, UseBroker(broker));
        var withoutDrive = Run(["journal", "--maximum-size", "5000", "--allocation-delta", "500"], out var withoutDriveResult, UseBroker(broker));

        Assert.AreEqual(0, result, Joined(grow));
        CollectionAssert.AreEqual(new[] { ("C", 5000L, 500L) }, grown);
        Assert.AreEqual(2, grow.Count(line => line == "Journal C: maximum 1000, allocation delta 100"));
        Assert.IsTrue(grow.Contains("Grown: maximum 5000, allocation delta 500"), Joined(grow));
        Assert.AreEqual(1, withoutDriveResult, Joined(withoutDrive));
    }

    static InProcessBroker NewBroker(List<(string Drive, long Maximum, long Delta)> grown)
    {
        return new InProcessBroker(new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                _ => new UsnJournalCursor(7, 1000),
                (_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]],
                (_, since, _) => (Array.Empty<UsnJournalEntry>(), since),
                QueryVolumeInformation: _ => new NtfsVolumeInformation(1024 * 1000, 1024),
                GrowUsnJournal: (drive, maximum, delta) =>
                {
                    grown.Add((drive, maximum, delta));
                    return new UsnJournalSettings { MaximumSize = maximum, AllocationDelta = delta };
                }),
            processorCount: 4));
    }

    Action<DriveScanner> UseBroker(InProcessBroker broker)
    {
        return scanner =>
        {
            DriveScannerElevationNoticeTests.AcknowledgeDeliberately(scanner);
            scanner._isElevated = () => false;
            scanner._createBrokerSession = () => BrokerTestHarness.CreateSession(_ => Task.FromResult(broker.Process));
            scanner._resolveDrive = letter => new IndexedDrive(char.ToUpperInvariant(letter[0]), _directory, 4242);
            scanner._delay = (_, _) => Task.CompletedTask;
        };
    }

    [DataTestMethod]
    [DataRow(new[] { "search", "S", "--name" }, "--name needs a value.")]
    [DataRow(new[] { "search", "SS" }, "SS is not an option of an Index verb or a drive letter.")]
    public void Parse_Invalid_NamesTheProblem(string[] commandLine, string expected)
    {
        Assert.IsFalse(IndexVerbArguments.TryParse(commandLine, out _, out var error));
        Assert.AreEqual(expected, error);
        Assert.IsTrue(IndexVerbArguments.IsVerb("SEARCH"));
        Assert.IsFalse(IndexVerbArguments.IsVerb("find-git"));
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }

    string[] Enumerate(string verb, params string[] extra)
    {
        return [verb, "S", "--source", "enumeration", "--root", _tree, "--no-cache", .. extra];
    }

    static void NoDelay(DriveScanner scanner)
    {
        scanner._delay = (_, _) => Task.CompletedTask;
    }

    // Runs one command line with the cache directory owned by this test so the default-cache guard never trips.
    List<string> Run(string[] commandLine, out int result, Action<DriveScanner>? configure = null)
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _cacheDirectory = _cache,
            _writeLine = line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            }
        };
        configure?.Invoke(scanner);
        result = scanner.Run(commandLine);
        return lines;
    }

    static string Joined(IEnumerable<string> lines)
    {
        return string.Join(Environment.NewLine, lines);
    }
}
