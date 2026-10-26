using System.Text.RegularExpressions;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// The Watch sample's index verbs over an in-process broker with scripted volumes: watch, rescan, journal and cache,
// plus the elevation report that touches no volume.
[TestClass]
[DoNotParallelize]
public sealed partial class WatchVerbTests : IDisposable
{
    static readonly SyntheticJournalCursor Armed = new(7, 1000);

    TempDirectory _tempDirectory = null!;
    string TemporaryDirectoryPath => _tempDirectory.Path;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => _tempDirectory = new TempDirectory("watchVerbs");

    public void Dispose()
    {
        CacheDirectory.Hooks.ResetToDefaults();
        _tempDirectory.Dispose();
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Run_Watch_PrintsStateChangesAndEachChangeWithItsKind()
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
        var clock = new AutoAdvanceClock(TimeSpan.FromSeconds(30), seen.Task);
        var host = HostOver(line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }

            if (line.StartsWith("  Created: ", StringComparison.Ordinal))
            {
                seen.TrySetResult();
            }
        }, clock);

        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        var cacheDir = Path.Combine(TemporaryDirectoryPath, "cache");
        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Watch, ["X"]) { CacheDirectory = cacheDir, Seconds = 30 }, session, FixtureDrive, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.Contains(line => line.StartsWith("  watch X: CaughtUp", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.Contains(line =>
            line.StartsWith("  Created: ", StringComparison.Ordinal) &&
            line.EndsWith(", drive X, entry new.txt", StringComparison.Ordinal), lines,
            string.Join(Environment.NewLine, lines));
        Assert.Contains(line => line.StartsWith("Saw 1 changes in 30 seconds;", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("Broker: connected.", lines);
        Assert.Contains("  start X: Succeeded", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("  stop X: Succeeded", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_Rescan_ScansAgainAndReportsTheCacheState()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var host = HostOver(lines.Add);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        var cacheDir = Path.Combine(TemporaryDirectoryPath, "cache");

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Rescan, ["X"]) { CacheDirectory = cacheDir }, session, FixtureDrive, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Index holds ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.Contains("=== Drive X: done ===", lines);
        Assert.AreEqual(2, lines.Count(line => line.StartsWith("Cache X: block ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.Contains(line => line.StartsWith("Watch: NotStarted v", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.Contains(line => line.Contains("recovery stopped False", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_JournalWithBothSizes_GrowsTheJournalThroughTheBroker()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes() with
        {
            GrowUsnJournal = (_, target) => target
        });
        var lines = new List<string>();
        var host = HostOver(lines.Add);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        var cacheDir = Path.Combine(TemporaryDirectoryPath, "cache");

        var result = await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Journal, ["X"]) { CacheDirectory = cacheDir, MaximumSize = 9000, AllocationDelta = 4096 }, session, FixtureDrive, CancellationToken.None);

        Assert.AreEqual(0, result);
        Assert.Contains("Journal X: maximum size 9000, allocation delta 4096", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_CacheAfterACachedOpen_ListsTheBlockAndClearRemovesIt()
    {
        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var cacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache");
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        await HostOver(_ => { }).RunThroughBrokerAsync(new WatchArguments(WatchVerb.Rescan, ["X"]) { CacheDirectory = cacheDirectory }, session, FixtureDrive, CancellationToken.None);

        var listing = new List<string>();
        var clearing = new List<string>();
        var afterwards = new List<string>();

        Assert.AreEqual(0, HostOver(listing.Add).Run(["cache", "--cache-directory", cacheDirectory]));
        Assert.AreEqual(0, HostOver(clearing.Add).Run(["cache", "X", "--cache-directory", cacheDirectory, "--clear"]));
        Assert.AreEqual(0, HostOver(afterwards.Add).Run(["cache", "--cache-directory", cacheDirectory]));

        Assert.ContainsSingle(line => line.StartsWith("X: ", StringComparison.Ordinal), listing, string.Join(Environment.NewLine, listing));
        Assert.Contains(line => line.StartsWith("X: ", StringComparison.Ordinal) && line.Contains(", policy policy-", StringComparison.Ordinal), listing, "The block lives in its policy folder and the listing names it.");
        Assert.ContainsSingle(line => line.Trim() == "X: Deleted",
clearing, string.Join(Environment.NewLine, clearing));
        Assert.DoesNotContain(line => line.StartsWith("Deleted block file '", StringComparison.Ordinal), clearing,
            "Cache clearing reports deletion through results only: " + string.Join(Environment.NewLine, clearing));
        Assert.AreEqual(0, afterwards.Count(line => line.StartsWith("X: ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(CachedBlockDeletionOutcome.InUse)]
    [DataRow(CachedBlockDeletionOutcome.Failed)]
    public void Run_CacheClearWithAnUnavailableBlock_AttemptsEveryPolicyAndReturnsOne(CachedBlockDeletionOutcome unavailableOutcome)
    {
        var root = Path.Combine(TemporaryDirectoryPath, "cache");
        var unavailable = CreateCacheBlock(root, 'T');
        var available = new[]
        {
            CreateCacheBlock(root, 'U'),
            CreateCacheBlock(Path.Combine(root, "policy-first"), 'V'),
            CreateCacheBlock(Path.Combine(root, "policy-second"), 'W')
        };
        using var owner = unavailableOutcome == CachedBlockDeletionOutcome.InUse
            ? BlockOwnerLock.TryAcquire(unavailable)
            : null;
        if (unavailableOutcome == CachedBlockDeletionOutcome.InUse)
        {
            Assert.IsNotNull(owner);
        }

        var listing = new List<string>();
        Assert.AreEqual(0, HostOver(listing.Add).Run(["cache", "--cache-directory", root]));
        Assert.AreEqual(4, listing.Count(line => line.Contains(" bytes, ", StringComparison.Ordinal)));
        Assert.DoesNotContain(line => line.StartsWith("  ", StringComparison.Ordinal), listing);
        Assert.IsTrue(available.Append(unavailable).All(File.Exists));

        CacheDirectory.Hooks._beforeDelete = path =>
        {
            if (path == unavailable && unavailableOutcome == CachedBlockDeletionOutcome.Failed)
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
            }
        };
        var clearing = new List<string>();

        var result = HostOver(clearing.Add).Run(["cache", "--clear", "--cache-directory", root]);

        var outcomes = clearing.Where(line => line.StartsWith("  ", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(4, outcomes, string.Join(Environment.NewLine, clearing));
        foreach (var letter in new[] { 'U', 'V', 'W' })
        {
            Assert.ContainsSingle(line => line == $"  {letter}: Deleted", outcomes);
        }
        Assert.ContainsSingle(line => unavailableOutcome == CachedBlockDeletionOutcome.Failed
            ? line.StartsWith("  T: Failed (", StringComparison.Ordinal) && line.EndsWith(')')
            : line == "  T: InUse", outcomes);
        Assert.IsTrue(available.All(path => !File.Exists(path)));
        Assert.IsTrue(unavailableOutcome == CachedBlockDeletionOutcome.InUse
            ? File.Exists(unavailable)
            : Directory.Exists(unavailable));
        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void Run_CacheClearWithoutBlocks_ReturnsZero()
    {
        var root = Path.Combine(TemporaryDirectoryPath, "cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "policy-empty"));
        var lines = new List<string>();

        Assert.AreEqual(0, HostOver(lines.Add).Run(["cache", "--clear", "--cache-directory", root]));

        Assert.IsEmpty(lines);
    }

    static string CreateCacheBlock(string directory, char letter)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CacheDirectory.BlockFileName(letter, 1));
        File.WriteAllText(path, "deliberately invalid block content");
        return path;
    }

    [TestMethod]
    public async Task Run_RescanAfterJournalRecreation_PrintsGrowthTargetRefusal()
    {
        await using (var handle = BrokerTestHarness.StartInProcess(Volumes()))
        {
            var sLaunch = Task.FromResult(handle);
            var s = BrokerTestHarness.CreateSession(_ => sLaunch);
            Assert.AreEqual(0, await HostOver(_ => { }).RunThroughBrokerAsync(new WatchArguments(WatchVerb.Rescan, ["X"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, s, FixtureDrive, CancellationToken.None));
        }

        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(8, 0, 2000, 4096, 8192));
        await using var replacement = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var replacementSessionLaunch = Task.FromResult(replacement);
        var replacementSession = BrokerTestHarness.CreateSession(_ => replacementSessionLaunch);

        Assert.AreEqual(0, await HostOver(lines.Add).RunThroughBrokerAsync(new WatchArguments(WatchVerb.Rescan, ["X"]) { CacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache") }, replacementSession, FixtureDrive, CancellationToken.None));

        Assert.Contains("Journal growth target unavailable: CheckpointNotTrimmed", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_ElevationStatus_ReportsTheProcessWithoutLaunchingAnything()
    {
        using var attended = new UnattendedEnvironmentScope(null);
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null)
        {
            _isElevated = () => false,
            _canSelfElevate = () => true
        };

        var result = host.Run(["elevation-status"]);

        Assert.AreEqual(0, result);
        Assert.Contains($"Process: {Environment.ProcessPath}", lines);
        Assert.Contains("Elevated: False; can self-elevate: True; unattended: False", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_CachedModes_UseTheSampleProfileRootForProductionAndInventory()
    {
        var root = SampleHost.CacheDirectoryFromProfile(TemporaryDirectoryPath);
        Assert.AreEqual(Path.Combine(TemporaryDirectoryPath, ".MFTLib.Sample.Watch", "cache"), root);

        await using var handle = BrokerTestHarness.StartInProcess(Volumes());
        var lines = new List<string>();
        var host = HostOver(lines.Add);
        var sessionLaunch = Task.FromResult(handle);
        var session = BrokerTestHarness.CreateSession(_ => sessionLaunch);
        Assert.AreEqual(0, await host.RunThroughBrokerAsync(new WatchArguments(WatchVerb.Rescan, ["X"]) { CacheDirectory = root }, session, FixtureDrive, CancellationToken.None));
        lines.Clear();

        Assert.AreEqual(0, host.Run(["cache", "--clear", "--cache-directory", root]));

        Assert.IsTrue(Directory.Exists(root));
        Assert.Contains(line => line.StartsWith("X: ", StringComparison.Ordinal) && line.Contains(root, StringComparison.Ordinal), lines);
        Assert.Contains(line => line.Trim() == "X: Deleted", lines);

        var hostSource = string.Join("\n", new[]
        {
            "SampleProgram.Watch/SampleHost.Run.cs",
            "SampleProgram.Watch/SampleHost.Verbs.cs",
            "SampleProgram.Watch/SampleHost.Broker.cs"
        }.Select(RepositoryFiles.Read));
        Assert.AreEqual(1, MyRegex().Count(hostSource),
            "Run resolves the cache root; cache and broker verbs consume that value.");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t")]
    public void Run_CacheWithoutAProfile_RequiresAnExplicitDirectory(string? profile)
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            SampleHost.CacheDirectoryFromProfile(profile));
        Assert.IsTrue(exception.Message.Contains("--cache-directory", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SampleCacheDirectory_FromSuppliedProfile_IsPure()
    {
        var profile = Path.Combine(TemporaryDirectoryPath, "absent-profile");

        Assert.AreEqual(Path.Combine(profile, ".MFTLib.Sample.Watch", "cache"), SampleHost.CacheDirectoryFromProfile(profile));
        Assert.IsFalse(Directory.Exists(profile));
    }

    // The run is attended and unelevated, so the heads-up dialog precedes the broker launch.
    IndexedDrive FixtureDrive(string letter) => new(letter[0], TemporaryDirectoryPath, 4242);

    static SampleHost HostOver(Action<string> writeLine, TimeProvider? timeProvider = null)
    {
        var host = new SampleHost(new LineCollectingWriter([], writeLine), TextReader.Null, timeProvider ?? new FakeTimeProvider())
        {
            _isElevated = () => false
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

    [GeneratedRegex(@"\bCacheDirectoryFromProfile\(profileDirectory\)")]
    private static partial Regex MyRegex();
}
