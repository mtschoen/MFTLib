using System.Text.RegularExpressions;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The Index verbs over an enumeration source: a small real directory tree, no volume, no elevation, no
// broker. Each test runs one command line through DriveScanner.Run and reads the lines it printed.
[TestClass]
[DoNotParallelize]
public class DriveScannerIndexVerbTests
{
    const string Drive = "S";

    string _directory = null!;
    string _tree = null!;
    string _cache = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"indexVerbs-{Guid.NewGuid():N}");
        _tree = Path.Combine(_directory, "tree");
        _cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(Path.Combine(_tree, "docs", "sub"));
        Directory.CreateDirectory(Path.Combine(_tree, "other"));
        File.WriteAllText(Path.Combine(_tree, "docs", "readme.md"), "hello world");
        File.WriteAllBytes(Path.Combine(_tree, "docs", "big.bin"), new byte[5000]);
        File.WriteAllText(Path.Combine(_tree, "docs", "sub", "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_tree, "other", "readme.md"), "dup");
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
    public void Search_Substring_PrintsTheEffectiveQueryThenTheMatches()
    {
        var lines = Run(Verb("search", "--name", "readme"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        CollectionAssert.IsSubsetOf(
            new[] { "Effective query:", "  name pattern: readme (a substring)", "  case sensitive: False", "  under: the whole index",
                    "  kind: files and directories", "  size: any to any bytes" }, lines);
        Assert.IsTrue(lines.Any(line => line.StartsWith("  " + Path.Combine(_tree, "docs", "readme.md"), StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("2 entries; 2 shown (limit 20)."), Joined(lines));
    }

    [TestMethod]
    public void Search_EveryFilter_AppearsInTheEffectiveQueryAndNarrowsTheResult()
    {
        var docs = Path.Combine(_tree, "docs");

        var lines = Run(Verb("search", "--name", "*.md", "--case-sensitive", "--under", docs, "--files", "--min-size", "1",
            "--max-size", "100", "--after", "2000-01-01", "--before", "2100-01-01"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("  name pattern: *.md (a glob that must match the whole name)"), Joined(lines));
        Assert.IsTrue(lines.Contains("  case sensitive: True"));
        Assert.IsTrue(lines.Contains($"  under: {docs}"));
        Assert.IsTrue(lines.Contains("  kind: files only"));
        Assert.IsTrue(lines.Contains("  size: 1 to 100 bytes"));
        Assert.IsTrue(lines.Contains("  modified: after 2000-01-01 00:00:00Z, before 2100-01-01 00:00:00Z"));
        Assert.IsTrue(lines.Contains("1 entries; 1 shown (limit 20)."), Joined(lines));
    }

    [TestMethod]
    public void Search_Directories_OnlyDirectoriesMatch()
    {
        var lines = Run(Verb("search", "--directories"), out _);

        Assert.IsTrue(lines.Contains("  kind: directories only"));
        Assert.IsTrue(lines.Contains("4 entries; 4 shown (limit 20)."), Joined(lines));
    }

    [TestMethod]
    public void Search_ExactName_UsesFindByNameAndSaysSo()
    {
        var lines = Run(Verb("search", "--exact", "--name", "readme.md", "--limit", "1"), out _);

        Assert.IsTrue(lines.Contains("Exact name match through FindByName(readme.md)."), Joined(lines));
        Assert.IsTrue(lines.Contains("2 entries; 1 shown (limit 1)."), Joined(lines));
    }

    [TestMethod]
    public void Search_UnderAPathThatIsNotIndexed_FailsWithTheReason()
    {
        var lines = Run(Verb("search", "--under", Path.Combine(_tree, "missing")), out var result);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error in search: InvalidOperationException: --under ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Enumerate_StopsAtTheLimitAndDisposesTheEnumerator()
    {
        var lines = Run(Verb("enumerate", "--limit", "2"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("Stopped early at the limit of 2; the enumerator was disposed with matches unread."),
            Joined(lines));
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("  after disposal: ", StringComparison.Ordinal) &&
                                              line.EndsWith("valid True disposed False", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Enumerate_ReadToTheEnd_ReportsEveryEntry()
    {
        var lines = Run(Verb("enumerate", "--limit", "100"), out _);

        Assert.IsTrue(lines.Contains("Enumeration finished with 8 entries; the enumerator was disposed."), Joined(lines));
    }

    [TestMethod]
    public void FindPath_PrintsEveryRootAndTheFoundEntry()
    {
        var readme = Path.Combine(_tree, "docs", "readme.md");

        var lines = Run(Verb("find-path", "--path", readme), out _);

        Assert.IsTrue(lines.Any(line => line.StartsWith($"Root of drive {Drive}: ", StringComparison.Ordinal)), Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith($"Find({readme}) found {readme} [name readme.md; key S:", StringComparison.Ordinal)),
            Joined(lines));
    }

    [TestMethod]
    public void FindPath_NothingThere_SaysSoAndStillPrintsTheRoot()
    {
        var missing = Path.Combine(_tree, "none.txt");

        var lines = Run(Verb("find-path", "--path", missing), out var result);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains($"Find({missing}) found nothing."));
    }

    [TestMethod]
    public void FindPath_NoPath_PrintsOnlyTheRoot()
    {
        var lines = Run(Verb("find-path"), out var result);

        Assert.AreEqual(0, result);
        Assert.IsFalse(lines.Any(line => line.StartsWith("Find(", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Tree_FromTheRoot_PrintsTheChildrenToTheRequestedDepth()
    {
        var lines = Run(Verb("tree", "--depth", "2", "--limit", "5"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Start: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  2 children of ", StringComparison.Ordinal)), Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("    3 children of docs;", StringComparison.Ordinal)), Joined(lines));
    }

    [TestMethod]
    public void Tree_ByPath_PrintsTheParentChainUpToTheRoot()
    {
        var lines = Run(Verb("tree", "--path", Path.Combine(_tree, "docs", "sub", "notes.txt")), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.AreEqual(3, lines.Count(line => line.StartsWith("  parent: ", StringComparison.Ordinal)), Joined(lines));
    }

    [TestMethod]
    public void Tree_ByRecordKey_SelectsThatEntryFromTheInventory()
    {
        var readme = Path.Combine(_tree, "docs", "readme.md");
        var found = Run(Verb("find-path", "--path", readme), out _);
        var key = Regex.Match(Joined(found), @"found .*key (S:\d+:Enumeration)").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(key));

        var lines = Run(Verb("tree", "--record-key", key), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains($"Selecting record key {key} from the inventory."));
        Assert.IsTrue(lines.Any(line => line.StartsWith($"Start: {readme} [name readme.md; key {key};", StringComparison.Ordinal)),
            Joined(lines));
    }

    [TestMethod]
    public void Tree_ByAKeyNoEntryHas_Fails()
    {
        var lines = Run(Verb("tree", "--record-key", "S:99999:Enumeration"), out var result);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("No entry in the index has the key S:99999:Enumeration")), Joined(lines));
    }

    [TestMethod]
    public void Open_AFile_PrintsItsFirstBytes()
    {
        var lines = Run(Verb("open", "--path", Path.Combine(_tree, "docs", "readme.md"), "--bytes", "5"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("Stream length 11; read the first 5 bytes:"), Joined(lines));
        Assert.IsTrue(lines.Contains("  hex  68656C6C6F"));
        Assert.IsTrue(lines.Contains("  text hello"));
    }

    [TestMethod]
    public void Open_ADirectory_ReportsThatItReadsFilesOnly()
    {
        var lines = Run(Verb("open", "--path", Path.Combine(_tree, "docs")), out var result);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("is a directory; open reads files.")), Joined(lines));
    }

    [TestMethod]
    public void Open_APathThatIsNotIndexed_Fails()
    {
        var lines = Run(Verb("open", "--path", Path.Combine(_tree, "ghost.txt")), out var result);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("is not in the index.")), Joined(lines));
    }

    [TestMethod]
    public void Largest_ListsTheBiggestFileFirst()
    {
        var lines = Run(Verb("largest", "--count", "1"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        var header = lines.IndexOf("Largest 1 files under the whole index:");
        Assert.IsTrue(header >= 0, Joined(lines));
        StringAssert.Contains(lines[header + 1], "big.bin");
        StringAssert.Contains(lines[header + 1], "size 5000");
    }

    [TestMethod]
    public void Largest_UnderADirectory_StaysInsideIt()
    {
        var other = Path.Combine(_tree, "other");

        var lines = Run(Verb("largest", "--count", "5", "--under", other), out _);

        Assert.IsTrue(lines.Contains($"Largest 5 files under {other}:"), Joined(lines));
        Assert.IsFalse(lines.Any(line => line.Contains("big.bin")));
    }

    [TestMethod]
    public void DuplicateNames_GroupsTheTwoReadmeFiles()
    {
        var lines = Run(Verb("duplicate-names"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("1 names occur more than once; 1 shown (limit 20)."), Joined(lines));
        Assert.IsTrue(lines.Contains("  readme.md: 2 entries"));
    }

    [TestMethod]
    public void Rescan_AllDrives_PrintsTheBatchResultsAndTheStatus()
    {
        var lines = Run(Verb("rescan"), out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("Rescanning all drives."));
        Assert.IsTrue(lines.Contains($"  drive {Drive}: Succeeded"), Joined(lines));
    }

    [TestMethod]
    public void Rescan_OneDrive_UsesTheSingleDriveOverload()
    {
        var lines = Run(Verb("rescan", "--drive-scope", Drive), out _);

        Assert.IsTrue(lines.Contains($"Rescanning single drive {Drive}."));
        Assert.IsTrue(lines.Contains($"  RescanAsync({Drive}) completed."), Joined(lines));
    }

    [TestMethod]
    public void Rescan_ADriveList_UsesTheListOverload()
    {
        var lines = Run(Verb("rescan", "--drive-list", Drive), out _);

        Assert.IsTrue(lines.Contains($"Rescanning drive list [{Drive}]."));
        Assert.IsTrue(lines.Contains($"  drive {Drive}: Succeeded"), Joined(lines));
    }

    [TestMethod]
    public void Watch_AllDrives_StartsWaitsObservesAndStopsWithoutRealSleeping()
    {
        var delays = new List<TimeSpan>();
        var lines = Run(Verb("watch", "--seconds", "3"), out var result, scanner =>
            scanner._delay = (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        Assert.AreEqual(0, result, Joined(lines));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, delays);
        Assert.IsTrue(lines.Contains("Watching all drives."));
        Assert.AreEqual(3, lines.Count(line => line == $"  drive {Drive}: NotApplicable"), Joined(lines));
        Assert.IsTrue(lines.Contains($"  drive {Drive}: no lost catch-up; the journal checkpoint is intact."));
    }

    [TestMethod]
    public void Watch_OneDrive_ReportsEachSingleDriveCallThatRefuses()
    {
        var lines = Run(Verb("watch", "--drive-scope", Drive, "--inspect-session"), out var result, NoDelay);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith($"  StartWatchingAsync({Drive}) failed: InvalidOperationException", StringComparison.Ordinal)),
            Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith($"  WaitForCatchUpAsync({Drive}) failed: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith($"  StopWatchingAsync({Drive}) failed: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("No broker session to inspect: this source runs without one."));
    }

    [TestMethod]
    public void Watch_ADriveList_UsesTheListOverloads()
    {
        var lines = Run(Verb("watch", "--drive-list", Drive), out var result, NoDelay);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains($"Watching drive list [{Drive}]."));
        Assert.AreEqual(3, lines.Count(line => line == $"  drive {Drive}: NotApplicable"), Joined(lines));
    }

    [TestMethod]
    public void JournalStatus_PrintsSizingFromTheJournalQueryAndTheLossState()
    {
        UsnJournalSettingsQuery._queryOverride = _ => new UsnJournalSettings { MaximumSize = 1000, AllocationDelta = 100 };
        try
        {
            var lines = Run(Verb("journal-status", "--wait-catch-up"), out var result);

            Assert.AreEqual(0, result, Joined(lines));
            Assert.IsTrue(lines.Contains($"  drive {Drive}: maximum size 1000 bytes, allocation delta 100 bytes"), Joined(lines));
            Assert.IsTrue(lines.Contains($"  drive {Drive}: no lost catch-up; the journal checkpoint is intact."));
        }
        finally
        {
            UsnJournalSettingsQuery._queryOverride = null;
        }
    }

    [TestMethod]
    public void JournalStatus_TheQueryFails_PrintsWhyAndContinues()
    {
        UsnJournalSettingsQuery._queryOverride = _ => throw new IOException("volume busy");
        try
        {
            var lines = Run(Verb("journal-status"), out var result);

            Assert.AreEqual(0, result);
            Assert.IsTrue(lines.Contains($"  drive {Drive}: journal sizing unavailable: IOException: volume busy"), Joined(lines));
        }
        finally
        {
            UsnJournalSettingsQuery._queryOverride = null;
        }
    }

    [TestMethod]
    public void Open_WithTheCacheOn_WritesABlockThatCacheInspectListsAndCacheClearDeletes()
    {
        Run(CachedSearch(), out _);

        var matching = Run(["cache-inspect", "--cache-directory", _cache, "--cache-tag", "TEST:1", Drive], out var inspectResult);
        var different = Run(["cache-inspect", "--cache-directory", _cache, "--cache-tag", "TEST:2"], out _);
        var cleared = Run(["cache-clear", "--cache-directory", _cache], out var clearResult);
        var afterwards = Run(["cache-inspect", "--cache-directory", _cache], out _);

        Assert.AreEqual(0, inspectResult, Joined(matching));
        Assert.IsTrue(matching.Contains("InspectCached over drives [S] returned 1 blocks; with the rejection callback 1 blocks and 0 rejected files."),
            Joined(matching));
        Assert.IsTrue(matching.Any(line => line.StartsWith("    availability Available; validation Valid (the block is complete and consistent); producer Enumeration;", StringComparison.Ordinal)),
            Joined(matching));
        Assert.IsTrue(matching.Contains("    cache tag \"TEST\" v1 against expected \"TEST\" v1: == True; != False; Equals True"),
            Joined(matching));
        Assert.IsTrue(different.Contains("    cache tag \"TEST\" v1 against expected \"TEST\" v2: == False; != True; Equals False"),
            Joined(different));
        Assert.AreEqual(0, clearResult, Joined(cleared));
        Assert.IsTrue(cleared.Contains("1 files: Deleted"), Joined(cleared));
        Assert.IsTrue(cleared.Contains("1 cached blocks examined."));
        Assert.IsTrue(afterwards.Contains("InspectCached over every drive returned 0 blocks; with the rejection callback 0 blocks and 0 rejected files."),
            Joined(afterwards));
    }

    [TestMethod]
    public void CacheInspect_AFileThatIsNotABlock_IsListedAsInvalidWithItsReason()
    {
        Directory.CreateDirectory(_cache);
        File.WriteAllText(Path.Combine(_cache, "S-00000001.mlix"), "not a block file");

        var lines = Run(["cache-inspect", "--cache-directory", _cache], out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("InspectCached over every drive returned 1 blocks;", StringComparison.Ordinal)), Joined(lines));
        Assert.IsTrue(lines.Contains("    availability Invalid; validation WrongMagic (the file does not start with the block signature); producer unknown; root unknown"),
            Joined(lines));
        Assert.IsTrue(lines.Contains("    cache tag: none readable"));
    }

    [TestMethod]
    public void CacheInspect_EnsureCreated_CreatesTheNamedDirectoryOnlyWhenAsked()
    {
        var directory = Path.Combine(_directory, "fresh");

        var withoutFlag = Run(["cache-inspect", "--cache-directory", directory], out _);
        var existedBefore = Directory.Exists(directory);
        var withFlag = Run(["cache-inspect", "--cache-directory", directory, "--ensure-created"], out var result);

        Assert.IsFalse(existedBefore, Joined(withoutFlag));
        Assert.AreEqual(0, result, Joined(withFlag));
        Assert.IsTrue(Directory.Exists(directory));
        Assert.IsTrue(withFlag.Any(line => line.StartsWith("EnsureCreated returned ", StringComparison.Ordinal) &&
                                          line.EndsWith("it exists: True", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CacheInspect_NoDirectoryGiven_ResolvesTheDefaultPathAndTheTestGuardRefusesIt()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner { _writeLine = lines.Add };

        var result = scanner.Run(["cache-inspect"]);

        Assert.AreEqual(1, result, Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error in cache-inspect: InvalidOperationException", StringComparison.Ordinal)),
            Joined(lines));
    }

    [TestMethod]
    public void Search_UnavailableSourceOverAWarmCache_AnswersFromTheCachedBlock()
    {
        Run(CachedSearch(), out _);

        var lines = Run(["search", Drive, "--source", "unavailable", "--root", _tree, "--cache-directory", _cache,
            "--cache-tag", "TEST:1", "--cache-only", "--limit", "1"], out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Contains("8 entries; 1 shown (limit 1)."), Joined(lines));
    }

    [TestMethod]
    public void Search_UnavailableSourceWithNoCache_ReportsTheReasonItWasGiven()
    {
        var lines = Run([ "search", Drive, "--source", "unavailable", "--root", _tree, "--no-cache",
            "--unavailable-reason", "no scanner here" ], out _);

        Assert.IsTrue(lines.Any(line => line.Contains("no scanner here")), Joined(lines));
    }

    [TestMethod]
    public void ElevationStatus_PrintsWhatTheProcessKnows()
    {
        var lines = Run(["elevation-status"], out var result);

        Assert.AreEqual(0, result, Joined(lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Elevated: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Can relaunch itself elevated: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains($"Default wait for an elevated copy: {ElevationUtilities.DefaultElevatedTimeout}."));
    }

    [TestMethod]
    public void ElevationStatus_RelaunchUnattended_SkipsTheRelaunch()
    {
        var lines = Run(["elevation-status", "--relaunch-elevated"], out _, scanner =>
            scanner._getEnvironmentVariable = name => name == DriveScanner.UnattendedVariableName ? "1" : null);

        Assert.IsTrue(
            lines.Contains($"Running unattended ({DriveScanner.UnattendedVariableName}=1): the elevated relaunch was skipped.") ||
            lines.Contains("Already elevated; nothing to relaunch."), Joined(lines));
    }

    [TestMethod]
    public void ElevationStatus_RelaunchCancelledAtTheDialog_DoesNotRelaunch()
    {
        var lines = Run(["elevation-status", "--relaunch-elevated"], out _, scanner =>
        {
            DriveScannerElevationNoticeTests.AcknowledgeDeliberately(scanner);
            var acknowledged = scanner._messageBox;
            scanner._messageBox = (handle, text, title, style) =>
            {
                acknowledged(handle, text, title, style);
                return DriveScanner.MessageBoxResultCancel;
            };
        });

        Assert.IsTrue(
            lines.Contains("The elevated relaunch was cancelled at the heads-up dialog.") ||
            lines.Contains("Already elevated; nothing to relaunch."), Joined(lines));
    }

    [TestMethod]
    public void Run_BrokerSourceUnattendedAndNotElevated_SkipsTheBrokerLaunchLikeScanDrive()
    {
        var lines = Run(["search", Drive], out var result, scanner =>
        {
            scanner._isElevated = () => false;
            scanner._getEnvironmentVariable = name => name == DriveScanner.UnattendedVariableName ? "1" : null;
        });

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Running unattended", StringComparison.Ordinal)), Joined(lines));
    }

    [TestMethod]
    public void Run_BrokerSourceDeclinedAtTheDialog_PrintsTheElevationFailure()
    {
        var lines = Run(["search", Drive], out var result, scanner =>
        {
            DriveScannerElevationNoticeTests.AcknowledgeDeliberately(scanner);
            var acknowledged = scanner._messageBox;
            scanner._isElevated = () => false;
            scanner._messageBox = (handle, text, title, style) =>
            {
                acknowledged(handle, text, title, style);
                return DriveScanner.MessageBoxResultCancel;
            };
        });

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Contains("AUTOMATIC ELEVATION FAILED."), Joined(lines));
    }

    [TestMethod]
    public void Run_AnIndexVerbWithABadOption_PrintsTheProblemAndTheUsage()
    {
        var lines = Run(["search", Drive, "--bogus"], out var result);

        Assert.AreEqual(2, result);
        Assert.IsTrue(lines.Contains("--bogus does not apply to search."));
        Assert.IsTrue(lines.Any(line => line.Contains("Index verbs: <verb> [drive ...] [options]")), Joined(lines));
    }

    string[] Verb(string verb, params string[] extra)
    {
        return [verb, Drive, "--source", "enumeration", "--root", _tree, "--no-cache", .. extra];
    }

    // An enumeration search with the cache on, which leaves a tagged block in the test's cache directory.
    string[] CachedSearch()
    {
        return ["search", Drive, "--source", "enumeration", "--root", _tree, "--cache-directory", _cache,
                "--cache-tag", "TEST:1", "--limit", "1"];
    }

    static void NoDelay(DriveScanner scanner)
    {
        scanner._delay = (_, _) => Task.CompletedTask;
    }

    // Runs one command line, with the cache directory owned by this test so the default-cache guard never trips.
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
