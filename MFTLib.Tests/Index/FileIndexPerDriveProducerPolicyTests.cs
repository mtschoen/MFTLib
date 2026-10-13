using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexPerDriveProducerPolicyTests
{
    readonly OwnedIndexDirectories _directories = new();
    static readonly DateTime Moment = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(_directories.TreeRoot);
        File.WriteAllText(Path.Combine(_directories.TreeRoot, "walked.txt"), "owned tree");
    }

    [TestCleanup]
    public void Cleanup() => _directories.Dispose();

    static SyntheticMftProducer Producer() => new(_ =>
    [
        new SyntheticRow(5, ".", 5) { IsDirectory = true },
        new SyntheticRow(6, "scanned.txt", 5)
    ])
    {
        Completed = Moment,
        JournalCursor = new SyntheticJournalCursor(7, 4096)
    };

    IndexedDrive Drive(char letter, ProducerPolicy policy) => new(letter, _directories.TreeRoot, 123, policy);

    FileIndexOptions Options(params IndexedDrive[] drives) => new()
    {
        Drives = drives,
        CacheDirectory = _directories.CacheDirectory,
        MftSource = SyntheticIndexSource.Create(Producer())
    };

    [TestMethod]
    public async Task MixedOpen_EachDriveUsesItsProducerAndQueriesCombineTheirRows()
    {
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerPolicy.Mft), Drive('U', ProducerPolicy.Enumeration)), CancellationToken.None);

        AssertDrive(index, 'T', ProducerKind.Mft);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
        var rows = index.Search(new SearchQuery(".txt"));
        CollectionAssert.AreEquivalent(new[] { "scanned.txt", "walked.txt" }, rows.Select(row => row.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { 'T', 'U' }, rows.Select(row => row.RecordKey.Drive.Letter).ToArray());

        await index.RescanAsync(['T'], CancellationToken.None);
        await index.RescanAsync(['U'], CancellationToken.None);
        AssertDrive(index, 'T', ProducerKind.Mft);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
    }

    [TestMethod]
    public async Task MixedOpen_MftFailureNeverWalksItsTreeAndEnumerationSiblingIsReady()
    {
        var options = Options(Drive('T', ProducerPolicy.Mft), Drive('U', ProducerPolicy.Enumeration)) with
        {
            MftSource = MftIndexSource.Unavailable("scan refused")
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var failed = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.IsNull(failed.Block);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(new DriveKey('U'), index.Search(new SearchQuery("walked.txt")).Single().RecordKey.Drive);
    }

    [TestMethod]
    public async Task EnumerationOnly_NullMftSourceScansEveryDrive()
    {
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerPolicy.Enumeration), Drive('U', ProducerPolicy.Enumeration)) with
        {
            MftSource = null
        }, CancellationToken.None);

        AssertDrive(index, 'T', ProducerKind.Enumeration);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(2, index.Search(new SearchQuery("walked.txt")).Count);
    }

    [TestMethod]
    public async Task MixedOpen_NullMftSourceFailsOnlyTheDriveNeedingAScan()
    {
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerPolicy.Mft), Drive('U', ProducerPolicy.Enumeration)) with
        {
            MftSource = null
        }, CancellationToken.None);

        var failed = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.AreEqual("ProducerPolicy.Mft requires FileIndexOptions.MftSource to be set.", failed.Failure?.Message);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
    }

    [TestMethod]
    public async Task WarmMftDrive_NullSourceNeedsNeitherScanNorWatchUntilStartIsRequested()
    {
        var options = Options(Drive('T', ProducerPolicy.Mft));
        await using (var seeded = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            AssertDrive(seeded, 'T', ProducerKind.Mft);
        }

        await using var index = await FileIndex.OpenAsync(options with { MftSource = null }, CancellationToken.None);
        AssertDrive(index, 'T', ProducerKind.Mft, BlockSource.WarmStartedFromCache);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], CancellationToken.None));
        Assert.IsFalse(index.Drives.Single().Watch.Requested);
    }

    [TestMethod]
    [DataRow(ProducerPolicy.Mft, false)]
    [DataRow(ProducerPolicy.Enumeration, false)]
    [DataRow(ProducerPolicy.Mft, true)]
    [DataRow(ProducerPolicy.Enumeration, true)]
    public async Task CacheProducerMismatch_ColdScansOrDeclinesCacheOnly(ProducerPolicy requested, bool cacheOnly)
    {
        var stored = requested == ProducerPolicy.Mft ? ProducerPolicy.Enumeration : ProducerPolicy.Mft;
        var options = Options(Drive('T', stored));
        await using (var seeded = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            AssertDrive(seeded, 'T', stored == ProducerPolicy.Mft ? ProducerKind.Mft : ProducerKind.Enumeration);
        }

        var diagnostics = new List<string>();
        await using var index = await FileIndex.OpenAsync(options with
        {
            Drives = [Drive('T', requested)],
            InitialOpenCacheOnly = cacheOnly,
            Diagnostics = diagnostics.Add
        }, CancellationToken.None);

        if (cacheOnly)
        {
            var failed = index.Drives.Single();
            Assert.AreEqual(DriveState.Failed, failed.State);
            Assert.AreEqual(DriveFailureKind.CacheDeclined, failed.Failure?.Kind);
            Assert.IsNull(failed.Block);
            Assert.AreEqual(0, index.Search(new SearchQuery(null)).Count);
        }
        else
        {
            AssertDrive(index, 'T', requested == ProducerPolicy.Mft ? ProducerKind.Mft : ProducerKind.Enumeration);
            var expectedName = requested == ProducerPolicy.Mft ? "scanned.txt" : "walked.txt";
            Assert.AreEqual(expectedName, index.Search(new SearchQuery(".txt")).Single().Name);
        }

        Assert.IsTrue(diagnostics.Any(line => line.Contains("producer kind mismatch", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DumpDrive_EnumerationPolicyIsRejectedBeforeProduction()
    {
        var source = MftIndexSources.FromMftDumpFile(Path.Combine(_directories.TreeRoot, "missing.dump"), 'D');
        var drive = Drive('D', ProducerPolicy.Enumeration) with { RootDirectory = "dump:/D", VolumeSerial = 0 };
        var failure = await Assert.ThrowsExceptionAsync<ArgumentException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [drive],
            NoCache = true,
            MftSource = source
        }, CancellationToken.None));
        StringAssert.StartsWith(failure.Message, "Dump sources require ProducerPolicy.Mft.");
    }

    [TestMethod]
    public async Task MixedWatch_EnumerationIsUnsupportedWhileMftSiblingStartsAndCatchesUp()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerPolicy.Mft), Drive('U', ProducerPolicy.Enumeration)) with
        {
            MftSource = SyntheticIndexSource.Create(Producer(), watches)
        }, CancellationToken.None);

        Assert.IsFalse(index.Drives.Single(drive => drive.Drive == 'U').Watch.Supported);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['U'], CancellationToken.None));
        var results = await index.StartWatchingAsync(index.Drives.Select(drive => drive.Drive), CancellationToken.None);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results.Single(result => result.Drive == 'U').Outcome);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results.Single(result => result.Drive == 'T').Outcome);
        await index.WaitForCatchUpAsync(['T'], CancellationToken.None);
        Assert.AreEqual(WatchState.CaughtUp, index.Drives.Single(drive => drive.Drive == 'T').Watch.State);
        Assert.AreEqual(new DriveKey('T'), watches.Starts.Single().Drive);
        await index.RescanAsync(['U'], CancellationToken.None);
        Assert.AreEqual(WatchState.CaughtUp, index.Drives.Single(drive => drive.Drive == 'T').Watch.State);
        Assert.IsFalse(index.Drives.Single(drive => drive.Drive == 'U').Watch.Supported);
    }

    static void AssertDrive(FileIndex index, char letter, ProducerKind kind,
        BlockSource source = BlockSource.ProducedByScan)
    {
        var status = index.Drives.Single(drive => drive.Drive == letter);
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.IsNotNull(status.Block);
        Assert.AreEqual(source, status.Block.Source);
        Assert.AreEqual(kind, SyntheticIndexInspection.ReadHeader(index, letter)!.ProducerKind);
    }
}
